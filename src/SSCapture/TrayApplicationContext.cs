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
    private CaptureDelivery? deferredFeedback;
    private readonly CaptureHistory history = new();
    private readonly CaptureRecipeBook recipes = new();
    private readonly ToolStripMenuItem sameSettingsItem;
    private readonly RegionStore regionStore;
    private readonly ToolStripMenuItem savedRegionMenu;
    private SavedRegion? lastRegion;
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
    private long captureSerial, lastRememberedSerial;
    private string? recordingFailure;
    private StatusMessage? lastStatus;
    private TrayOperationPolicy OperationPolicy => new(
        busy, recorder is not null, recordingTransition, namingForm is not null, pendingRecording is not null);
    public TrayApplicationContext(bool showSettings)
    {
        _ = dispatcher.Handle;
        try { NotificationImage.CleanupPreviousSession(workRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lastStatus = new StatusMessage("以前の通知用画像を整理できません: " + ex.Message, true, DateTimeOffset.Now); }
        feedback = new CaptureFeedback(message => Post(() => StoreStatus(message, true)));
        store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "settings.json"));
        output = new OutputRouter(workRoot, capturedWithBounds: (image, path, time, bounds) =>
        {
            if (settings?.KeepHistory == true)
                history.Add(image, path, time, bounds);
        }, delivered: DispatchDelivery, retryChanged: () => Post(RefreshMenu),
            captureSucceeded: (sequence, recipe) => { recipes.Remember(sequence, recipe); Post(RefreshMenu); });
        settings = store.Load(out var warning);
        regionStore = new RegionStore(Path.Combine(AppContext.BaseDirectory, "data", "last-region.json"));
        lastRegion = regionStore.Load(out var regionWarning);
        warning ??= regionWarning;
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

        recordItem = new ToolStripMenuItem("録画", null, (_, _) => OnCaptureKey(CaptureIntent.Recording));
        menu.Items.Add(recordItem);
        savedRegionMenu = new ToolStripMenuItem("範囲を再撮影");
        menu.Items.Add(savedRegionMenu);
        sameSettingsItem = new ToolStripMenuItem("直前と同じ設定で再撮影", null, (_, _) => _ = TakeSameSettingsAsync());
        menu.Items.Add(sameSettingsItem);
        menu.Items.Add("入力プリセット", null, (_, _) => ShowRegions());
        menu.Items.Add("撮影履歴", null, (_, _) => ShowHistory());
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
        settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SSCapture.ShowSettings");
        signalWait = ThreadPool.RegisterWaitForSingleObject(settingsSignal, (_, _) => Post(ShowSettings), null, Timeout.Infinite, false);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (!TryConfigureHook(settings.HasShortcuts, out string? hookError))
            warning = hookError;
        RefreshMenu();
        // 自動起動の警告は記録だけにし、設定画面を勝手に開かない。
        if (warning is not null)
            Post(() => StoreStatus(warning, true));
        if (showSettings)
            Post(ShowSettings);
        management = new("ss-capture", dispatcher, ShowSettings, ExitThread, () => OperationPolicy.MustWaitToStop ? "撮影・録画を完了またはキャンセルしてから終了してください。" : null);
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
