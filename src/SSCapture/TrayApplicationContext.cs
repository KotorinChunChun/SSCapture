using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly KotorinToys.Integration.ManagementEndpoint management;
    private readonly NotifyIcon tray;
    private readonly Icon icon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem pause, recordItem;
    private readonly SettingsStore store;
    private readonly OutputRouter output;
    private readonly CaptureFeedback feedback;
    private CaptureDelivery? deferredFeedback;
    private readonly CaptureHistory history = new();
    private readonly RegionStore regionStore;
    private readonly ToolStripMenuItem savedRegionMenu;
    private SavedRegion? lastRegion;
    private RegionManagerForm? regionForm;
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

    public TrayApplicationContext(bool showSettings)
    {
        _ = dispatcher.Handle;
        feedback = new CaptureFeedback(message => Post(() => StoreStatus(message, true)));
        store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "settings.json"));
        output = new OutputRouter(workRoot, capturedWithBounds: (image, path, time, bounds) =>
        {
            if (settings?.KeepHistory == true) history.Add(image, path, time, bounds);
        }, delivered: DispatchDelivery);
        settings = store.Load(out var warning);
        regionStore = new RegionStore(Path.Combine(AppContext.BaseDirectory, "data", "last-region.json"));
        lastRegion = regionStore.Load(out var regionWarning);
        warning ??= regionWarning;
        icon = AppIcon.Create();
        menu = new ContextMenuStrip { Font = new Font("Yu Gothic UI", 12F) };
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
        menu.Items.Add("登録範囲", null, (_, _) => ShowRegions());
        menu.Items.Add("撮影履歴", null, (_, _) => ShowHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("設定", null, (_, _) => ShowSettings());
        menu.Items.Add("保存先を開く", null, (_, _) => OpenFolder());
        pause = new ToolStripMenuItem("ショートカットを一時停止", null, (_, _) => ToggleShortcut());
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Text = "SSCapture", Icon = icon, ContextMenuStrip = menu, Visible = true };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SSCapture.ShowSettings");
        signalWait = ThreadPool.RegisterWaitForSingleObject(settingsSignal, (_, _) => Post(ShowSettings), null, Timeout.Infinite, false);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (!TryConfigureHook(settings.HasShortcuts, out string? hookError)) warning = hookError;
        RefreshMenu();
        if (warning is not null) Post(() => Report(warning, true));
        else if (showSettings) Post(ShowSettings);
        management = new("ss-capture", dispatcher, ShowSettings, ExitThread,
            () => busy || recorder is not null || recordingTransition ? "撮影・録画を完了またはキャンセルしてから終了してください。" : null);
    }
    private void Post(Action action)
    {
        if (exiting || dispatcher.IsDisposed) return;
        try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void NotifyDelivery(CaptureDelivery result)
    {
        if (exiting) return;
        if (busy || namingForm is not null || pendingRecording is not null || recordingTransition)
        {
            deferredFeedback = result;
            return;
        }
        feedback.Notify(result, settings);
    }
    private void DispatchDelivery(CaptureDelivery result)
    {
        if (exiting || dispatcher.IsDisposed) return;
        if (dispatcher.InvokeRequired) Post(() => NotifyDelivery(result));
        else NotifyDelivery(result);
    }
    private void FlushFeedback()
    {
        if (exiting || busy || namingForm is not null || pendingRecording is not null || recordingTransition || deferredFeedback is null) return;
        var result = deferredFeedback;
        deferredFeedback = null;
        feedback.Notify(result, settings);
    }
    internal void HandleUnexpectedError(Exception exception)
    {
        foreach (var overlay in Application.OpenForms.OfType<CaptureOverlay>().ToArray()) overlay.Close();
        Report("処理を中断しました: " + exception.Message, true);
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)) return;
        Post(() =>
        {
            bool enabled = hook?.Enabled ?? settings.HasShortcuts;
            hook?.Dispose(); hook = null;
            if (!TryConfigureHook(enabled, out var error)) Report(error!, true);
            RefreshMenu();
        });
    }
    private void OnCaptureKey(CaptureIntent intent)
    {
        if (WindowVisibility.RestorePending(namingForm)) return;
        if (intent == CaptureIntent.Recording && recorder is not null)
        {
            if (recorder.IsRecording && !recordingTransition) _ = StopRecordingAsync();
            return;
        }
        if (pendingRecording is not null) { if (recordingForm is not null) WindowVisibility.Show(recordingForm); return; }
        _ = TakeCaptureAsync(settings.DefaultMode, intent);
    }
    private bool TryConfigureHook(bool enabled, out string? error)
    {
        try
        {
            if (enabled && hook is null) hook = NewHook(settings);
            if (hook is not null) hook.Enabled = enabled;
            error = null; return true;
        }
        catch (Exception ex) { error = "ショートカットを開始できません: " + ex.Message; return false; }
    }
    private KeyboardHook NewHook(AppSettings value, bool enabled = true) => new(intent => Post(() => OnCaptureKey(intent)), value, enabled,
        number => Post(() => _ = TakeInstantAsync(number)),
        id => Post(() => _ = TakeRegionAsync(id)));

    private async Task TakeInstantAsync(int number)
    {
        if (exiting || WindowVisibility.RestorePending(namingForm)) return;
        if (pendingRecording is not null) { WindowVisibility.RestorePending(recordingForm); return; }
        if (busy) return;
        busy = true;
        feedback.HideForCapture();
        RefreshMenu();
        try
        {
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            Rectangle bounds = InstantCapture.ResolveDisplay(number, InstantCapture.ReadDisplays());
            using Bitmap image = InstantCapture.Take(bounds, settings.IncludeCursor);
            // Keep capture exclusive until output completes. The optional protected
            // completion toast is shown only after pixels have been acquired.
            StoreStatus(await InstantCapture.DeliverAsync(image, settings, capturedAt, output, regionName: $"画面{number}", captureBounds: bounds));
        }
        catch (Exception ex) { StoreStatus("即時撮影できませんでした: " + ex.Message, true); }
        finally { busy = false; if (!exiting) { RefreshMenu(); FlushFeedback(); } }
    }
    private async Task TakeRegionAsync(string id)
    {
        if (exiting || WindowVisibility.RestorePending(namingForm)) return;
        if (pendingRecording is not null) { WindowVisibility.RestorePending(recordingForm); return; }
        if (busy) return;
        busy = true; feedback.HideForCapture(); RefreshMenu();
        try
        {
            var region = id == AppSettings.LastRegionKeyId ? lastRegion : settings.SavedRegions.FirstOrDefault(r => r.Id == id);
            if (region is null) throw new InvalidOperationException("撮影範囲がありません。先に矩形で撮影するか、範囲を登録してください。");
            Rectangle bounds = RegionCoordinates.Resolve(region, RegionCoordinates.ReadDisplays());
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            using var image = InstantCapture.Take(bounds, settings.IncludeCursor);
            StoreStatus(await InstantCapture.DeliverAsync(image, settings, capturedAt, output, regionName: region.Name, captureBounds: bounds));
        }
        catch (Exception ex) { StoreStatus("範囲を再撮影できませんでした: " + ex.Message, true); }
        finally { busy = false; if (!exiting) { RefreshMenu(); FlushFeedback(); } }
    }

    private void ShowRegions()
    {
        if (exiting || busy) return;
        if (regionForm is null || regionForm.IsDisposed)
        {
            regionForm = new RegionManagerForm(settings.SavedRegions, lastRegion, size => PickRegionAsync(size, settings.FixedCaptureAnchor),
                regions => SaveSettings(settings with { SavedRegions = regions }), selectAnchoredRegion: PickRegionAsync);
            regionForm.FormClosed += (_, _) => regionForm = null;
        }
        regionForm.UpdateLastRegion(lastRegion);
        WindowVisibility.Show(regionForm);
    }
    private async Task<Rectangle?> PickRegionAsync(Size? size, CaptureAnchor anchor)
    {
        if (busy || pendingRecording is not null) throw new InvalidOperationException("撮影・録画を終了してから範囲を選択してください。");
        busy = true; feedback.HideForCapture(); RefreshMenu(); form?.Hide(); historyForm?.Hide(); menu.Close();
        try
        {
            using var outcome = await CaptureSession.SelectAsync(new CaptureOptions(size is null ? CaptureMode.Rectangle : CaptureMode.FixedRegion,
                CaptureIntent.Recording, OutputDestination.None, FixedSize: size, FixedAnchor: anchor,
                LimitToStartingMonitor: settings.LimitToStartingMonitor));
            return outcome.Selection?.Bounds;
        }
        finally { busy = false; if (!exiting) { RefreshMenu(); FlushFeedback(); } }
    }
    private void ShowHistory()
    {
        if (exiting || busy) return;
        if (historyForm is null || historyForm.IsDisposed)
        {
            historyForm = new HistoryForm(history, CopyHistoryAsync, EditHistoryAsync);
            historyForm.FormClosed += (_, _) => historyForm = null;
        }
        historyForm.RefreshEntries();
        WindowVisibility.Show(historyForm);
    }
    private async Task<string> CopyHistoryAsync(Guid id)
    {
        var entry = history.Entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        try { await ClipboardWriter.CopyAsync(image); }
        catch
        {
            NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.Clipboard, CaptureBounds: entry.CaptureBounds));
            throw;
        }
        NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, CaptureBounds: entry.CaptureBounds));
        return "コピーしました。";
    }
    private async Task<string> EditHistoryAsync(Guid id)
    {
        var entry = history.Entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        if (entry.SavedPath is string saved && File.Exists(saved))
        {
            try
            {
                using var process = Process.Start(OutputRouter.EditorStartInfo(settings.ImageEditorPath, saved, settings.ImageEditorArguments))
                    ?? throw new IOException("画像編集アプリを開始できません。");
            }
            catch
            {
                NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.ExternalApp, CaptureBounds: entry.CaptureBounds));
                throw;
            }
            NotifyDelivery(new(CaptureIntent.Screenshot, OutputDestination.ExternalApp, OutputDestination.None, CaptureBounds: entry.CaptureBounds));
            return "保存済みファイルを画像編集アプリへ送りました。";
        }
        using var image = history.CloneImage(id) ?? throw new InvalidOperationException("この画像は履歴から削除されています。");
        return await new OutputRouter(workRoot, delivered: DispatchDelivery)
            .ScreenshotAsync(image, OutputDestination.ExternalApp, settings with { KeepHistory = false, OpenFolderAfterSave = false }, captureBounds: entry.CaptureBounds);
    }
    private void ShowSettings()
    {
        if (exiting || busy) return;
        if (lastStatus?.IsExpired(DateTimeOffset.Now) == true) lastStatus = null;
        if (form is null || form.IsDisposed)
        {
            form = new SettingsForm(settings, next => SaveSettings(next with { SavedRegions = settings.SavedRegions }), OpenFolder,
                openRegions: ShowRegions, openHistory: ShowHistory);
            form.FormClosed += (_, _) => form = null;
            form.SetStatus(lastStatus);
        }
        WindowVisibility.Show(form);
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }
    private string? SaveSettings(AppSettings next)
    {
        if (recorder is not null || pendingRecording is not null) return "録画を終了またはキャンセルしてから保存してください。";
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
            if (!settings.ShowCaptureNotification) feedback.HideForCapture();
            if (!settings.KeepHistory) { history.Clear(); historyForm?.RefreshEntries(); }
            hook.Enabled = settings.HasShortcuts;
            RefreshMenu();
            return null;
        }
        catch (Exception ex) { replacement?.Dispose(); return "設定を保存できません: " + ex.Message; }
    }
    private void ToggleShortcut()
    {
        if (!TryConfigureHook(!(hook?.Enabled ?? false), out var error)) { Report(error!, true); return; }
        RefreshMenu();
    }
    private void RefreshMenu()
    {
        pause.Enabled = settings.HasShortcuts;
        pause.Checked = hook is null || !hook.Enabled;
        foreach (var (mode, item) in modeItems) { item.Checked = settings.DefaultMode == mode; item.Enabled = !busy && pendingRecording is null; }
        recordItem.Text = recorder?.IsPaused == true ? "録画を停止（一時停止中）" : recorder?.IsRecording == true ? "録画を停止" : pendingRecording is not null ? "録画準備" : "録画";
        recordItem.Enabled = !busy && !recordingTransition;
        foreach (ToolStripItem item in savedRegionMenu.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
        savedRegionMenu.DropDownItems.Clear();
        var lastItem = new ToolStripMenuItem("前回の範囲", null, (_, _) => _ = TakeRegionAsync(AppSettings.LastRegionKeyId)) { Enabled = lastRegion is not null };
        savedRegionMenu.DropDownItems.Add(lastItem);
        foreach (var region in settings.SavedRegions)
            savedRegionMenu.DropDownItems.Add(new ToolStripMenuItem(region.Name, null, (_, _) => _ = TakeRegionAsync(region.Id)));
        savedRegionMenu.Enabled = !busy && pendingRecording is null;
        tray.Text = recorder?.IsPaused == true ? "SSCapture — 録画一時停止中" : recorder?.IsRecording == true ? "SSCapture — 録画中" : pause.Checked ? "SSCapture — ショートカット停止中" : "SSCapture";
    }
    private async Task TakeCaptureAsync(CaptureMode mode, CaptureIntent intent)
    {
        if (busy || exiting || pendingRecording is not null) return;
        busy = true;
        feedback.HideForCapture();
        RefreshMenu();
        form?.Hide();
        regionForm?.Hide(); historyForm?.Hide();
        menu.Close();
        bool openSettings = false;
        DateTimeOffset capturedAt = DateTimeOffset.Now;
        long sequence = ++captureSerial;
        IReadOnlyList<DisplayLayout>? captureDisplays = null;
        try { captureDisplays = RegionCoordinates.ReadDisplays(); } catch (Exception) { /* Capture remains available without reusable coordinates. */ }
        try
        {
            using CaptureOutcome outcome = await CaptureSession.SelectAsync(new CaptureOptions(mode, intent, settings.DefaultDestination, settings.FreezeFrame,
                IncludeRecordingImage: intent == CaptureIntent.Recording && FileNamePattern.UsesOcr(settings.FileNamePattern),
                IncludeCursor: settings.IncludeCursor, FixedSize: new Size(settings.FixedWidth, settings.FixedHeight), FixedAnchor: settings.FixedCaptureAnchor,
                LimitToStartingMonitor: settings.LimitToStartingMonitor));
            openSettings = outcome.OpenSettings;
            if (outcome.Selection is null) { StoreStatus("選択をキャンセルしました。"); return; }
            if (outcome.Selection.Destination == OutputDestination.None) { Report("出力先がすべてOFFです。", false); return; }
            string? fileName = null;
            FileNamePlan? namePlan = null;
            AppSettings captureSettings = settings;
            if (outcome.Selection.Destination.HasFlag(OutputDestination.File))
            {
                if (FileNamePattern.UsesOcr(settings.FileNamePattern) && intent == CaptureIntent.Recording)
                {
                    using var naming = new OcrNameForm(outcome.Image ?? throw new InvalidOperationException("OCR用画像がありません。"), settings, intent, capturedAt);
                    namingForm = naming;
                    DialogResult result;
                    try { result = naming.ShowDialog(); }
                    finally { namingForm = null; }
                    if (result != DialogResult.OK) { StoreStatus("OCR命名をキャンセルしました。"); return; }
                    fileName = naming.FileName;
                    namePlan = naming.NamePlan;
                    captureSettings = naming.OutputSettings;
                }
                else if (!FileNamePattern.UsesOcr(settings.FileNamePattern)) namePlan = new FileNamePlan(settings.FileNamePattern, capturedAt, intent);
            }
            if (intent == CaptureIntent.Recording) { recordingFileName = fileName; recordingNamePlan = namePlan; PrepareRecording(outcome.Selection, captureSettings); }
            else if (outcome.Image is not null)
            {
                SavedRegion? remembered = null;
                if (mode is CaptureMode.Rectangle or CaptureMode.FixedRegion && outcome.Selection.Polygon is null && captureDisplays is not null)
                {
                    try
                    {
                        remembered = RegionCoordinates.FromBounds("前回範囲", outcome.Selection.Bounds, captureDisplays);
                        _ = RegionCoordinates.Resolve(remembered, RegionCoordinates.ReadDisplays());
                    }
                    catch (Exception) { remembered = null; }
                }
                var namingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Task<ScreenshotFileName?>>? requestFileName = outcome.Selection.Destination.HasFlag(OutputDestination.File)
                    && FileNamePattern.UsesOcr(captureSettings.FileNamePattern)
                    ? async () =>
                    {
                        try { return await RequestScreenshotFileNameAsync(outcome.Image, captureSettings, capturedAt); }
                        finally { namingFinished.TrySetResult(); }
                    } : null;
                var delivery = DeliverAndRememberAsync(outcome.Image, outcome.Selection.Destination, captureSettings,
                    fileName, namePlan, remembered, sequence, capturedAt, outcome.Selection.Bounds, requestFileName);
                Report(await AwaitDeliveryAsync(delivery, requestFileName is null ? null : namingFinished.Task), false);
            }
        }
        catch (Exception ex) { Report("撮影できませんでした: " + ex.Message, true); }
        finally
        {
            busy = false;
            if (!exiting) { RefreshMenu(); FlushFeedback(); if (openSettings) ShowSettings(); }
        }
    }
    private Task<ScreenshotFileName?> RequestScreenshotFileNameAsync(Bitmap image, AppSettings captureSettings, DateTimeOffset capturedAt)
    {
        using var naming = new OcrNameForm(image, captureSettings, CaptureIntent.Screenshot, capturedAt);
        namingForm = naming;
        try
        {
            return Task.FromResult<ScreenshotFileName?>(naming.ShowDialog() == DialogResult.OK
                ? new(naming.FileName, naming.NamePlan, naming.OutputSettings.OpenFolderAfterSave) : null);
        }
        finally { namingForm = null; }
    }
    private async Task<string> DeliverAndRememberAsync(Bitmap image, OutputDestination destination, AppSettings captureSettings,
        string? fileName, FileNamePlan? namePlan, SavedRegion? remembered, long sequence, DateTimeOffset capturedAt, Rectangle captureBounds,
        Func<Task<ScreenshotFileName?>>? requestFileName = null)
    {
        string warning = "";
        string result = await output.ScreenshotAsync(image, destination, captureSettings, fileName, namePlan, capturedAt, () =>
        {
            if (remembered is null || sequence <= lastRememberedSerial) return;
            try { regionStore.Save(remembered); lastRegion = remembered; lastRememberedSerial = sequence; if (!exiting) RefreshMenu(); }
            catch (Exception ex) { warning = Environment.NewLine + "前回範囲を保存できませんでした: " + ex.Message; }
        }, captureBounds: captureBounds, requestFileName: requestFileName);
        return result + warning;
    }
    private void PrepareRecording(CaptureSelection selection, AppSettings captureSettings)
    {

        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) throw new FileNotFoundException("録画エンジンがありません。setup-media.ps1 と build.ps1 を実行してください。");
        pendingRecording = selection;
        recordingSettings = captureSettings;
        recordingForm = new RecordingControlForm(selection.Bounds, captureSettings);
        recordingForm.StartRequested += () => _ = StartRecordingAsync();
        recordingForm.StopRequested += () => _ = StopRecordingAsync();
        recordingForm.CancelRequested += CancelRecording;
        recordingForm.PauseRequested += () => _ = ChangeRecordingPauseAsync(true);
        recordingForm.ResumeRequested += () => _ = ChangeRecordingPauseAsync(false);
        recordingForm.SystemAudioChangeRequested += enabled => _ = ChangeRecordingAudioAsync(true, enabled);
        recordingForm.MicrophoneChangeRequested += enabled => _ = ChangeRecordingAudioAsync(false, enabled);
        WindowVisibility.Show(recordingForm);
    }
    private async Task StartRecordingAsync()
    {
        if (pendingRecording is null || recordingTransition || recorder is not null) return;
        feedback.HideForCapture();
        recordingTransition = true;
        recordingFailure = null;
        try
        {
            recordingForm?.SetProcessing("録画を開始しています…");
            RefreshMenu();
            var current = new RecordingService(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe"), workRoot);
            recorder = current;
            current.Failed += message => Post(() =>
            {
                if (!ReferenceEquals(recorder, current)) return;
                recordingFailure ??= message;
                Report("録画エラー: " + message, true);
                if (!recordingTransition) _ = FinishFailedRecordingAsync();
            });
            await recorder.StartAsync(pendingRecording, recordingSettings!);
            if (recordingFailure is null)
            {
                recordingForm?.SetRecording();
                StoreStatus("録画中");
            }
        }
        catch (Exception ex)
        {
            await ReleaseRecordingAsync();
            Report("録画を開始できませんでした: " + ex.Message, true);
        }
        finally { recordingTransition = false; RefreshMenu(); FlushFeedback(); }
        if (recordingFailure is not null && recorder is not null) await FinishFailedRecordingAsync();
    }
    private async Task ChangeRecordingPauseAsync(bool paused)
    {
        if (recorder is null || recordingTransition || recorder.IsPaused == paused) return;
        recordingTransition = true;
        try
        {
            recordingForm?.SetProcessing(paused ? "一時停止しています…" : "録画を再開しています…");
            RefreshMenu();
            if (paused) await recorder.PauseAsync();
            else await recorder.ResumeAsync();
            if (recordingFailure is null)
            {
                if (paused) recordingForm?.SetPaused();
                else recordingForm?.SetRecording();
                StoreStatus(paused ? "録画を一時停止しました。" : "録画を再開しました。");
            }
        }
        catch (Exception ex)
        {
            await ReleaseRecordingAsync();
            Report((paused ? "録画を一時停止できませんでした: " : "録画を再開できませんでした: ") + ex.Message, true);
        }
        finally { recordingTransition = false; RefreshMenu(); FlushFeedback(); }
        if (recordingFailure is not null && recorder is not null) await FinishFailedRecordingAsync();
    }
    private async Task ChangeRecordingAudioAsync(bool systemAudio, bool enabled)
    {
        if (pendingRecording is null || recordingSettings is null || recordingTransition) return;
        recordingTransition = true;
        try
        {
            if (recorder is not null)
            {
                recordingForm?.SetProcessing((systemAudio ? "PC音声" : "マイク") + "を切り替えています…", pauseElapsed: false);
                RefreshMenu();
                if (systemAudio) await recorder.SetSystemAudioAsync(enabled);
                else await recorder.SetMicrophoneAsync(enabled);
            }
            recordingSettings = systemAudio ? recordingSettings with { RecordSystemAudio = enabled }
                : recordingSettings with { RecordMicrophone = enabled };
        }
        catch (Exception ex)
        {
            Report((systemAudio ? "PC音声" : "マイク") + "を切り替えられませんでした: " + ex.Message, true);
        }
        finally
        {
            if (recordingSettings is { } currentSettings)
            {
                recordingForm?.SetAudioState(currentSettings.RecordSystemAudio, currentSettings.RecordMicrophone);
                recordingForm?.SetControlsReady();
            }
            recordingTransition = false;
            RefreshMenu(); FlushFeedback();
        }
        if (recordingFailure is not null && recorder is not null) await FinishFailedRecordingAsync();
    }
    private async Task StopRecordingAsync()
    {
        if (recorder is null || recordingTransition) return;
        recordingTransition = true;
        try
        {
            recordingForm?.SetProcessing("動画を仕上げています…");
            RefreshMenu();
            string path = await recorder.StopAsync();
            try { Report(await AwaitDeliveryAsync(output.RecordingAsync(path, pendingRecording!.Destination, recordingSettings!, recordingFileName, recordingNamePlan, captureBounds: pendingRecording.Bounds)), false); }
            catch (Exception ex) { Report($"動画の送信に失敗しました: {ex.Message}\n動画: {path}", true); }
        }
        catch (Exception ex) { Report("録画を終了できませんでした: " + ex.Message, true); }
        finally { await ReleaseRecordingAsync(); recordingTransition = false; RefreshMenu(); FlushFeedback(); }
    }
    private async Task FinishFailedRecordingAsync()
    {
        if (recorder is null || recordingTransition) return;
        recordingTransition = true;
        try { await ReleaseRecordingAsync(); }
        finally { recordingTransition = false; RefreshMenu(); FlushFeedback(); }
    }
    private void CancelRecording()
    {
        if (recordingTransition || recorder is not null) return;
        recordingForm?.Finish(); recordingForm = null;
        pendingRecording = null; recordingSettings = null;
        StoreStatus("録画準備をキャンセルしました。");
        RefreshMenu();
        FlushFeedback();
    }
    private async Task<string> AwaitDeliveryAsync(Task<string> delivery, Task? interactionFinished = null)
    {
        if (await DeliveryWait.CompletedWithinAsync(delivery, TimeSpan.FromSeconds(10), interactionFinished)) return await delivery;
        _ = ObserveDeliveryAsync(delivery);
        return "送信先が応答しません。処理はバックグラウンドで継続しています。";
    }
    private async Task ObserveDeliveryAsync(Task<string> delivery)
    {
        try { string result = await delivery; Post(() => Report(result, false)); }
        catch (Exception ex) { Post(() => Report("バックグラウンド送信に失敗しました: " + ex.Message, true)); }
    }
    private async Task ReleaseRecordingAsync()
    {
        var previous = recorder;
        recorder = null;
        try { if (previous is not null) await previous.DisposeAsync(); }
        catch (Exception ex) { Report("録画の後処理に失敗しました: " + ex.Message, true); }
        finally
        {
            recordingForm?.Finish(); recordingForm = null;
            pendingRecording = null; recordingSettings = null;
        }
    }
    private void StoreStatus(string text, bool error = false)
    {
        if (exiting) return;
        lastStatus = new StatusMessage(text, error, DateTimeOffset.Now);
        form?.SetStatus(lastStatus);
    }
    private void Report(string text, bool error)
    {
        if (exiting) return;
        StoreStatus(text, error);
        if (error)
        {
            bool wasBusy = busy; busy = false; ShowSettings(); busy = wasBusy;
        }
    }
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.SaveDirectory);
            Process.Start(new ProcessStartInfo(settings.SaveDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Report("保存先を開けません: " + ex.Message, true); }
    }
    protected override void ExitThreadCore()
    {
        if (busy || recorder is not null || recordingTransition)
        { Report("撮影・録画を完了またはキャンセルしてから終了してください。", true); if (recordingForm is not null) WindowVisibility.Show(recordingForm); return; }
        CancelRecording();
        exiting = true;
        feedback.HideForCapture(); deferredFeedback = null;
        hook?.Dispose(); hook = null;
        tray.Visible = false;
        form?.Close();
        regionForm?.Close(); historyForm?.Close();
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            exiting = true;
            management.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            signalWait.Unregister(null); settingsSignal.Dispose();
            hook?.Dispose(); tray.Dispose(); icon.Dispose(); menu.Dispose();
            form?.Dispose(); recordingForm?.Dispose(); dispatcher.Dispose();
            regionForm?.Dispose(); historyForm?.Dispose(); history.Dispose();
            feedback.Dispose();
        }
        base.Dispose(disposing);
    }
}
