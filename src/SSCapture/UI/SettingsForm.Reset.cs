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
        if (PageDraft(page) is { } readDraft)
            AddApplyButton(actions, page, readDraft, () => RestorePageDraft(page), isBasic: page.Text == "基本");
        else if (page == workShortcutPage)
        {
            var revert = AddRevertButton(actions, () =>
            {
                presetShortcutEditor.LoadRows(committed.PresetCaptureShortcuts ?? []);
            });
            void Refresh() => revert.Enabled = !presetShortcutEditor.Rows.SequenceEqual(committed.PresetCaptureShortcuts ?? []);
            presetShortcutEditor.DraftChanged += (_, _) => Refresh();
            refreshSettingControls += Refresh;
            Refresh();
        }
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
                    LoadBasicDraft(defaults);
                    startupSettings.ResetDraft();
                    break;
                case "通知":
                    LoadNotificationDraft(defaults);
                    break;
                case "履歴":
                    keepHistory.Checked = defaults.KeepHistory;
                    historyRetentionDays.Value = defaults.HistoryRetentionDays;
                    persistHistory.Checked = defaults.PersistHistory;
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

    private void RestorePageDraft(TabPage page)
    {
        switch (page.Text)
        {
            case "基本":
                LoadBasicDraft(committed);
                startupSettings.RevertDraft();
                break;
            case "通知":
                LoadNotificationDraft(committed);
                break;
            case "履歴":
                keepHistory.Checked = committed.KeepHistory;
                historyRetentionDays.Value = committed.HistoryRetentionDays;
                persistHistory.Checked = committed.PersistHistory;
                break;
            case "外部アプリ":
                imageApps.LoadRestoredApplications(committed.ImageEditors, committed.SelectedImageEditorId);
                videoApps.LoadRestoredApplications(committed.VideoEditors, committed.SelectedVideoEditorId);
                presetEditor.SetDefaultApplication(true, imageApps.SelectedId);
                presetEditor.SetDefaultApplication(false, videoApps.SelectedId);
                presetEditor.RefreshApplications();
                imageApps.RefreshUsage();
                videoApps.RefreshUsage();
                break;
            case "即時撮影キー":
                lastRecipeKey.Value = committed.LastRecipeShortcut;
                for (int index = 0; index < instantKeys.Length; index++) instantKeys[index].Value = committed.InstantShortcuts[index];
                break;
            case FirstPointTab:
                captureBeforeKeys.ResetDraft(committed.CaptureBeforeKeys, committed.CaptureMouse);
                break;
            case SecondPointTab:
                captureKeys.ResetDraft(committed.CaptureKeys, committed.CaptureMouse);
                break;
        }
    }

    private void LoadNotificationDraft(AppSettings settings)
    {
        notificationDisplay.SelectedIndex = (int)settings.NotificationDisplay;
        notificationCorner.SelectedIndex = (int)settings.NotificationCorner;
        notificationDuration.Value = settings.NotificationDurationSeconds;
        holdNotification.Checked = settings.HoldNotificationWhileApproaching;
        playCaptureSound.Checked = settings.PlayCaptureSound;
        notificationApplication.SelectedIndex = Enumerable.Range(0, notificationApplication.Items.Count)
            .FirstOrDefault(index => ((NotificationAppItem)notificationApplication.Items[index]!).Id == settings.NotificationApplicationId);
    }

    private void LoadBasicDraft(AppSettings defaults)
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
        runAsAdministrator.Checked = defaults.RunAsAdministrator;
    }

    private void ResetShortcutDraft(AppSettings defaults)
    {
        switch (shortcutTabs.SelectedTab?.Text)
        {
            case "PC作業中":
                var rows = PresetCaptureShortcut.Migrate(defaults).PresetCaptureShortcuts!;
                var fixedShortcut = PresetCaptureShortcut.FindDefaultFixedAssignment(committed with
                {
                    PresetCaptureShortcuts = rows, InstantShortcuts = [], LastRecipeShortcut = ""
                });
                if (fixedShortcut is not null) rows = [.. rows, fixedShortcut];
                rows = PresetSettings.WithOcrShortcuts(committed, rows);
                HotkeyBinding.ValidateSettings(committed with { PresetCaptureShortcuts = rows });
                presetShortcutEditor.LoadRows(rows);
                break;
            case "即時撮影キー":
                HotkeyBinding.ValidateSettings(committed with
                {
                    LastRecipeShortcut = defaults.LastRecipeShortcut, InstantShortcuts = defaults.InstantShortcuts
                });
                lastRecipeKey.Value = defaults.LastRecipeShortcut;
                for (int index = 0; index < instantKeys.Length; index++) instantKeys[index].Value = defaults.InstantShortcuts[index];
                break;
            case FirstPointTab:
                captureBeforeKeys.ResetDraft(defaults.CaptureBeforeKeys, defaults.CaptureMouse);
                break;
            case SecondPointTab:
                captureKeys.ResetDraft(defaults.CaptureKeys, defaults.CaptureMouse);
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
