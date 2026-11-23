using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly KotorinToys.Integration.ManagementEndpoint management;
    private readonly NotifyIcon tray;
    private readonly Icon icon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem pause, recordItem, retryOutputItem;
    private readonly SettingsStore store;
    private readonly OutputRouter output;
    private readonly CaptureFeedback feedback;
    private readonly PinnedImageManager pinnedImages;
    private readonly ToolStripMenuItem pinnedVisibility;
    private CaptureDelivery? deferredFeedback;
    private readonly CaptureHistory history;
    private readonly System.Windows.Forms.Timer historyExpiration = new() { Interval = 30000 };
    private readonly CaptureRecipeBook recipes = new();
    private readonly ToolStripMenuItem sameSettingsItem;
    private readonly ToolStripMenuItem savedRegionMenu;
    private HistoryForm? historyForm;
    private readonly EventWaitHandle settingsSignal;
    private readonly RegisteredWaitHandle signalWait;
    private readonly Dictionary<CaptureMode, ToolStripMenuItem> modeItems = [];
    private readonly string workRoot = Path.Combine(AppContext.BaseDirectory, "data", "work");
    private AppSettings settings;
    private KeyboardHook? hook;
    private SettingsForm? form;
    private OcrNameForm? namingForm;
    private RecordingControlForm? recordingForm;
    private RecordingService? recorder;
    private CaptureSelection? pendingRecording;
    private AppSettings? recordingSettings;
    private string? recordingFileName;
    private FileNamePlan? recordingNamePlan;
    private bool busy, recordingTransition, exiting;
    private string? recordingFailure;
    private StatusMessage? lastStatus;
    private TrayOperationPolicy OperationPolicy => new(
        busy, recorder is not null, recordingTransition, namingForm is not null, pendingRecording is not null);
    internal TrayApplicationContext(bool showSettings, SettingsStore store, AppSettings initialSettings, string? warning)
    {
        this.store = store;
        settings = initialSettings;
        _ = dispatcher.Handle;
        history = new(storage: new(Path.Combine(AppContext.BaseDirectory, "data", "history")),
            onStorageError: message => Post(() => StoreStatus(message, true)));
        ConfigureHistory(restore: warning is null || settings.PersistHistory);
        historyExpiration.Tick += (_, _) => { if (history.Prune()) historyForm?.RefreshEntries(); };
        try { NotificationImage.CleanupPreviousSession(workRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lastStatus = new StatusMessage("以前の通知用画像を整理できません: " + ex.Message, true, DateTimeOffset.Now); }
        feedback = new CaptureFeedback(message => Post(() => StoreStatus(message, true)), savedFileMoved: SavedFileMoved);
        pinnedImages = new PinnedImageManager((message, error) => Post(() => StoreStatus(message, error)));
        output = new OutputRouter(workRoot, capturedWithBounds: (image, path, time, bounds) =>
        {
            history.Add(image, path, time, bounds);
        }, delivered: DispatchDelivery, retryChanged: () => Post(RefreshMenu),
            captureSucceeded: (sequence, recipe) =>
            {
                recipes.Remember(sequence, recipe);
                Post(() =>
                {
                    form?.UpdateLastRegion(recipes.LastRegion);
                    RefreshMenu();
                });
            },
            pinImage: pinnedImages.Show);
        icon = AppIcon.Create();
        menu = new ContextMenuStrip
        {
            Font = new Font("Yu Gothic UI", 12F)
        };
        foreach (var value in Enum.GetValues<CaptureMode>())
        {
            CaptureMode selected = value;
            var item = new ToolStripMenuItem(SettingsForm.ModeName(selected), null, (_, _) => _ = TakeCaptureAsync(selected, CaptureIntent.Screenshot));
            menu.Items.Add(item);
            modeItems.Add(value, item);
        }

        InitializeMonitorMenu();
        recordItem = new ToolStripMenuItem("録画", null, (_, _) => OnCaptureKey(CaptureIntent.Recording));
        menu.Items.Add(recordItem);
        menu.Items.Add(CreateTimerMenu());
        savedRegionMenu = new ToolStripMenuItem("範囲を再撮影");
        menu.Items.Add(savedRegionMenu);
        menu.Items.Add(presetCaptureMenu);
        sameSettingsItem = new ToolStripMenuItem("直前の設定で再撮影", null, (_, _) => _ = TakeSameSettingsAsync());
        menu.Items.Add(sameSettingsItem);
        menu.Items.Add("入力プリセット", null, (_, _) => ShowRegions());
        AddPreviousCaptureMenu();
        menu.Items.Add("撮影履歴", null, (_, _) => ShowHistory());
        pinnedVisibility = new ToolStripMenuItem("すべてのピン留めを一時非表示", null, (_, _) => pinnedImages.ToggleVisibility()) { Enabled = false };
        menu.Items.Add(pinnedVisibility);
        pinnedImages.Changed += (_, _) =>
        {
            pinnedVisibility.Enabled = pinnedImages.Count > 0;
            pinnedVisibility.Text = pinnedImages.Hidden ? "すべてのピン留めを再表示" : "すべてのピン留めを一時非表示";
        };
        retryOutputItem = new ToolStripMenuItem("失敗した出力を再実行", null, (_, _) => _ = RetryOutputAsync());
        menu.Items.Add(retryOutputItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("設定", null, (_, _) => ShowSettings());
        menu.Items.Add("保存先を開く", null, (_, _) => OpenFolder());
        pause = new ToolStripMenuItem("ショートカットを一時停止", null, (_, _) => ToggleShortcut());
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        tray = new NotifyIcon
        {
            Text = "SSCapture",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowSettings();
        };
        settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsCommandLine.ShowSettingsSignalName);
        signalWait = ThreadPool.RegisterWaitForSingleObject(settingsSignal, (_, _) => Post(ShowSettings), null, Timeout.Infinite, false);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (!TryConfigureHook(settings.HasShortcuts, out string? hookError))
            warning = warning is null ? hookError : warning + Environment.NewLine + hookError;
        RefreshMenu();
        // 自動起動の警告は記録だけにし、設定画面を勝手に開かない。
        if (warning is not null)
            Post(() => StoreStatus(warning, true));
        if (showSettings)
            Post(ShowSettings);
        management = new(SettingsCommandLine.ManagementId, dispatcher, ShowSettings, ExitThread,
            () => OperationPolicy.MustWaitToStop ? "撮影・録画を完了またはキャンセルしてから終了してください。" : null,
            importSettings: ImportFromManagement, hotkeyBridgePid: () => hook?.HotkeyBridgePid);
    }

    private void Post(Action action)
    {
        if (exiting || dispatcher.IsDisposed)
            return;
        try
        {
            dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal void HandleUnexpectedError(Exception exception)
    {
        foreach (var overlay in Application.OpenForms.OfType<CaptureOverlay>().ToArray())
            overlay.Close();
        Report("処理を中断しました: " + exception.Message, true);
    }
}
