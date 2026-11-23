using SSCapture.UI;

namespace SSCapture.Services;

internal enum NotificationCorner { BottomRight, BottomLeft, TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomCenter }
internal enum NotificationClickAction { None, OpenFolder, ExternalApplication }
internal sealed record NotificationEditorChoice(string Name, Action Open);
internal sealed record NotificationActions(Action? OpenFolder, Action? Edit, string RepeatHint,
    Bitmap? Thumbnail = null, Func<Control, CancellationToken, Task>? Drag = null,
    Action? Retry = null, string? Heading = null, IReadOnlyList<NotificationEditorChoice>? Editors = null,
    Action? CopyPath = null, Action? CopyText = null, Action? CopyTable = null)
{
    internal int ButtonRows => ((OpenFolder is null ? 0 : 1) + (Edit is null ? 0 : 1)
        + (Retry is null ? 0 : 1) + (CopyPath is null ? 0 : 1) + 1) / 2;
    internal bool HasInteractiveSurface => ButtonRows != 0 || Thumbnail is not null;
}

internal interface ICaptureToast : IDisposable
{
    Rectangle DisplayBounds => Rectangle.Empty;
    int DisplayDpi => 96;
    bool IsEditorMenuOpen => false;
    void SetActions(NotificationActions actions) { }
    bool TryShow(string message, bool hasFailure, NotificationCorner corner, out string? warning,
        Action? activate = null, NotificationDisplay display = NotificationDisplay.Main, Rectangle? captureBounds = null);
    void Hide();
}

internal interface ICompletionSound : IDisposable
{
    void Request();
    string? LastWarning { get; }
}

internal interface IFeedbackTimer : IDisposable
{
    event Action? Elapsed;
    void Restart(int milliseconds);
    void Stop();
}

/// <summary>Owns one toast. Create and call Notify/HideForCapture on the UI thread.</summary>
internal sealed class CaptureFeedback : IDisposable
{
    private readonly Func<ICaptureToast> createToast;
    private readonly ICompletionSound sound;
    private readonly IFeedbackTimer timer;
    private readonly Action<string>? warning;
    private readonly Action<string> openSavedFile;
    private readonly ExternalEditor editor;
    private int notificationSerial;
    private ICaptureToast? toast;
    private volatile bool disposed;
    private string? lastWarning;
    private readonly Func<long> now;
    private readonly Func<Point> pointer;
    private NotificationLifetime? lifetime;
    private NotificationImage? pendingImage;
    private Bitmap? thumbnail;
    private CancellationTokenSource? ocrCopy;
    private NotificationActions? shownActions;
    private CaptureDelivery? previousScreenshot;
    private int draggingSerial;
    private string? shownSavedPath;
    private readonly Func<Control, string, bool>? dragFile;
    private readonly Func<bool> dragButtonDown;
    private readonly Action<string>? retainDragFile;
    private readonly Action<string, string?>? savedFileMoved;
    private readonly Action<string> copyPathText;

    internal CaptureFeedback(Action<string>? warning = null, Func<ICaptureToast>? createToast = null,
        ICompletionSound? sound = null, IFeedbackTimer? timer = null, Action<string>? openSavedFile = null,
        Func<long>? now = null, Func<Point>? pointer = null, Action<System.Diagnostics.ProcessStartInfo>? launch = null,
        Func<Control, string, bool>? dragFile = null, Func<bool>? dragButtonDown = null, Action<string>? retainDragFile = null,
        ExternalEditor? externalEditor = null, Action<string, string?>? savedFileMoved = null,
        Action<string>? copyPathText = null)
    {
        this.warning = warning;
        this.createToast = createToast ?? (() => new CaptureToastForm());
        this.sound = sound ?? new CompletionSound(warning: Warn);
        this.timer = timer ?? new FeedbackTimer();
        this.openSavedFile = openSavedFile ?? (path => ExplorerSelection.Open(path));
        editor = externalEditor ?? new(launch);
        this.now = now ?? (() => Environment.TickCount64);
        this.pointer = pointer ?? Capture.CaptureNative.CursorPosition;
        this.dragFile = dragFile;
        this.dragButtonDown = dragButtonDown ?? (() => (Control.MouseButtons & MouseButtons.Left) != 0);
        this.retainDragFile = retainDragFile;
        this.savedFileMoved = savedFileMoved;
        this.copyPathText = copyPathText ?? (path => Clipboard.SetText(path, TextDataFormat.UnicodeText));
        this.timer.Elapsed += PollLifetime;
    }

    internal string? LastWarning => lastWarning ?? sound.LastWarning;

    internal bool CanShowPreviousScreenshot(AppSettings settings) =>
        !disposed && settings.ShowCaptureNotification && previousScreenshot is not null;

    internal void Notify(CaptureDelivery result, AppSettings settings, Action? retryOutput = null)
    {
        if (disposed || (!result.HasSuccess && !result.HasFailure)) { result.NotificationImage?.Dispose(); return; }
        if (result.Intent == CaptureIntent.Screenshot)
            previousScreenshot = new(result.Intent, result.Completed, result.Failed, result.FolderOpenFailed,
                CaptureBounds: result.CaptureBounds, CaptureDescription: result.CaptureDescription, MetadataFailed: result.MetadataFailed);
        Show(result, settings, retryOutput, reference: false);
    }

    private bool showingOcr;

    internal void ShowOcrProgress(AppSettings settings, Rectangle? bounds)
    {
        if (disposed || !settings.ShowCaptureNotification || settings.NotificationDurationSeconds == 0) return;
        HideForCapture();
        try
        {
            toast ??= createToast();
            toast.SetActions(new(null, null, "", Heading: "文字認識"));
            showingOcr = toast.TryShow("OCR中....", false, settings.NotificationCorner, out var warning,
                display: settings.ShareCapturePresentation ? NotificationDisplay.Captured : settings.NotificationDisplay,
                captureBounds: bounds);
            if (!showingOcr && warning is not null) Warn(warning);
        }
        catch (Exception ex) { Warn("OCRの進行表示を出せません: " + ex.Message); }
    }

    internal void ShowPreviousScreenshot(AppSettings settings)
    {
        if (CanShowPreviousScreenshot(settings) && previousScreenshot is { } result) Show(result, settings, null, reference: true);
    }

    private void Show(CaptureDelivery result, AppSettings settings, Action? retryOutput, bool reference)
    {
        if (!showingOcr) HideForCapture();
        showingOcr = false;
        pendingImage = result.NotificationImage;
        if (!reference && settings.PlayCaptureSound && result.HasSuccess)
        {
            try { sound.Request(); }
            catch (Exception ex) { Warn("効果音を再生できません: " + ex.Message); }
        }
        if (!settings.ShowCaptureNotification) { HideForCapture(); return; }
        try
        {
            int serial = ++notificationSerial;
            var presentation = NotificationPresentation.Create(result, settings, pendingImage is not null);
            shownSavedPath = result.SavedPath;
            Action? folder = presentation.SavedFile is not null ? () => OpenSavedFile(serial) : null;
            Action? copyPath = File.Exists(presentation.SavedFile) ? () => CopySavedPath(serial) : null;
            Action? edit = presentation.Editor is { } app
                ? () => OpenApplication(serial, result, app) : null;
            toast ??= createToast();
            bool canDrag = result.Intent == CaptureIntent.Screenshot && result.HasSuccess
                && (pendingImage is not null || result.SavedPath is not null || result.NotificationPath is not null);
            if (canDrag)
            {
                try
                {
                    if (pendingImage is not null) thumbnail = pendingImage.CreateThumbnail();
                    else
                    {
                        using var fileImage = Image.FromFile(result.SavedPath ?? result.NotificationPath!);
                        thumbnail = NotificationImage.Thumbnail(fileImage);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException or UnauthorizedAccessException
                    or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
                { /* 消失・不正ファイルのプレビューは省略し、成功済みの出力結果を維持する。 */ }
            }
            string? heading = reference ? "前回の撮影結果" : !result.HasFailure ? null
                : result.HasSuccess ? "一部の出力に失敗" : "出力に失敗";
            NotificationEditorChoice[]? editorChoices = thumbnail is null ? null :
                NotificationPresentation.ScreenshotEditors(settings, editor)
                    .Select(candidate => new NotificationEditorChoice(candidate.Name, () => OpenApplication(serial, result, candidate))).ToArray();
            shownActions = new(folder, edit, presentation.RepeatHint, thumbnail,
                canDrag && thumbnail is not null ? (source, gesture) => DragAsync(serial, result, settings, source, gesture) : null,
                retryOutput is not null && result.HasFailure ? () =>
                {
                    if (disposed || serial != notificationSerial) return;
                    HideForCapture();
                    retryOutput();
                } : null, heading, editorChoices, CopyPath: copyPath,
                CopyText: thumbnail is null ? null : () => _ = CopyOcrAsync(serial, result, settings, false),
                CopyTable: thumbnail is null ? null : () => _ = CopyOcrAsync(serial, result, settings, true));
            toast.SetActions(shownActions);
            bool shared = result.CaptureSettings?.ShareCapturePresentation ?? settings.ShareCapturePresentation;
            var display = shared ? NotificationDisplay.Captured : settings.NotificationDisplay;
            if (!toast.TryShow(presentation.Message, presentation.HasFailure, settings.NotificationCorner, out string? error,
                null, display, result.CaptureBounds))
            {
                HideForCapture();
                Warn(error ?? "完了表示を画面取得から除外できないため、表示を中止しました。");
                return;
            }
            int duration = checked(settings.NotificationDurationSeconds * 1000);
            lifetime = new NotificationLifetime(this.now(), duration, settings.HoldNotificationWhileApproaching);
            timer.Restart(toast.DisplayBounds.IsEmpty ? duration : 50);
        }
        catch (Exception ex)
        {
            HideForCapture();
            Warn("完了表示を出せません: " + ex.Message);
        }
    }

    internal void HideForCapture()
    {
        if (disposed) return;
        showingOcr = false;
        notificationSerial++;
        ocrCopy?.Cancel();
        ocrCopy = null;
        shownActions = null;
        lifetime = null;
        shownSavedPath = null;
        timer.Stop();
        toast?.Hide();
        thumbnail?.Dispose(); thumbnail = null;
        pendingImage?.Dispose(); pendingImage = null;
    }

    private void PollLifetime()
    {
        if (disposed || lifetime is null || toast is null) return;
        if (draggingSerial == notificationSerial || ocrCopy is not null) return;
        if (toast.IsEditorMenuOpen) { timer.Restart(50); return; }
        try
        {
            if (toast.DisplayBounds.IsEmpty || lifetime.Expired(now(), pointer(), toast.DisplayBounds, toast.DisplayDpi)) HideForCapture();
            else timer.Restart(50);
        }
        catch (Exception ex) { HideForCapture(); Warn("通知の近接状態を確認できません: " + ex.Message); }
    }

    internal void UpdateSavedPath(string previous, string? current)
    {
        if (string.Equals(shownSavedPath, previous, StringComparison.OrdinalIgnoreCase)) shownSavedPath = current;
    }

    private void OpenSavedFile(int serial)
    {
        if (disposed || serial != notificationSerial) return;
        string? path = shownSavedPath;
        HideForCapture();
        try { openSavedFile(path ?? throw new FileNotFoundException("保存ファイルが見つかりません。")); }
        catch (Exception ex) { Warn("保存ファイルをExplorerで表示できません: " + ex.Message); }
    }

    private async Task CopyOcrAsync(int serial, CaptureDelivery result, AppSettings settings, bool asTable)
    {
        if (disposed || serial != notificationSerial || ocrCopy is not null || shownActions is null) return;
        timer.Stop();
        string? path = shownSavedPath ?? result.NotificationPath;
        using var operation = new CancellationTokenSource();
        ocrCopy = operation;
        string message;
        bool failed = false;
        try
        {
            toast?.TryShow("OCR中....", false, settings.NotificationCorner, out _, display:
                settings.ShareCapturePresentation ? NotificationDisplay.Captured : settings.NotificationDisplay, captureBounds: result.CaptureBounds);
            using var bitmap = pendingImage?.CloneImage() ?? new Bitmap(RequireSavedFile(path));
            string directory = result.CaptureSettings?.OneOcrDirectory ?? settings.OneOcrDirectory;
            message = await CaptureOcrCopy.CopyAsync(bitmap, directory, asTable, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            failed = true;
            message = (asTable ? "表をコピーできませんでした。 " : "文字をコピーできませんでした。 ") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(ocrCopy, operation)) ocrCopy = null;
        }
        if (disposed || operation.IsCancellationRequested || serial != notificationSerial || shownActions is null || toast is null) return;
        try
        {
            toast.SetActions(shownActions with { Heading = failed ? "OCRエラー" : "OCR" });
            bool shared = result.CaptureSettings?.ShareCapturePresentation ?? settings.ShareCapturePresentation;
            var display = shared ? NotificationDisplay.Captured : settings.NotificationDisplay;
            if (toast.TryShow(message, failed, settings.NotificationCorner, out string? error,
                display: display, captureBounds: result.CaptureBounds))
            {
                int duration = checked(settings.NotificationDurationSeconds * 1000);
                lifetime = new NotificationLifetime(now(), duration, settings.HoldNotificationWhileApproaching);
                timer.Restart(toast.DisplayBounds.IsEmpty ? duration : 50);
            }
            else
            {
                HideForCapture();
                if (error is not null) Warn(error);
            }
        }
        catch (Exception ex) { HideForCapture(); Warn(ex.Message); }
        if (failed) Warn(message);
    }

    private void CopySavedPath(int serial)
    {
        if (disposed || serial != notificationSerial) return;
        string? path = shownSavedPath;
        HideForCapture();
        try { copyPathText(RequireSavedFile(path)); }
        catch (Exception ex) { Warn("ファイルのパスをコピーできません: " + ex.Message); }
    }

    private static string RequireSavedFile(string? path)
    {
        if (path is not null)
        {
            string absolute = Path.GetFullPath(path);
            if (File.Exists(absolute)) return absolute;
        }
        throw new FileNotFoundException("保存ファイルが見つかりません。");
    }

    private async Task DragAsync(int serial, CaptureDelivery result, AppSettings settings, Control source, CancellationToken gesture)
    {
        if (disposed || serial != notificationSerial || draggingSerial != 0) return;
        draggingSerial = serial;
        timer.Stop();
        TemporaryHandoff? temporary = null;
        try
        {
            string path;
            string? savedPath = shownSavedPath;
            bool hasSavedFile = File.Exists(savedPath);
            if (hasSavedFile) path = savedPath!;
            else if (pendingImage is not null) path = (temporary = await pendingImage.PrepareForHandoffAsync()).Path;
            else path = result.NotificationPath ?? throw new IOException("ドラッグする画像がありません。");
            if (disposed || serial != notificationSerial || source.IsDisposed || gesture.IsCancellationRequested || !dragButtonDown()) return;
            Action? completeMove = temporary is null ? null : temporary.CompleteMove;
            bool dropped = dragFile?.Invoke(source, path) ?? NotificationFileDrag.Run(source, path, completeMove: completeMove,
                savedFileMoved: hasSavedFile ? savedFileMoved : null);
            if (dropped && !disposed && serial == notificationSerial) HideForCapture();
        }
        catch (Exception ex) { Warn("通知から画像を渡せません: " + ex.Message); }
        finally
        {
            if (temporary is not null) { temporary.Dispose(); retainDragFile?.Invoke(temporary.Path); }
            draggingSerial = 0;
            if (!disposed && serial == notificationSerial && toast is not null)
            {
                // キャンセル・失敗・ボタンを早く離した場合も満額で通知へ戻れる。
                lifetime = new NotificationLifetime(now(), checked(settings.NotificationDurationSeconds * 1000), settings.HoldNotificationWhileApproaching);
                timer.Restart(toast.DisplayBounds.IsEmpty ? settings.NotificationDurationSeconds * 1000 : 50);
            }
        }
    }

    private async void OpenApplication(int serial, CaptureDelivery result, ExternalApplication app)
    {
        if (disposed || serial != notificationSerial) return;
        string? savedPath = shownSavedPath;
        var image = pendingImage; pendingImage = null;
        HideForCapture();
        TemporaryHandoff? temporary = null;
        try
        {
            var open = editor.Resolve(app);
            string path;
            if (savedPath is { } saved && File.Exists(saved)) path = saved;
            else if (image is not null) path = (temporary = await image.PrepareForHandoffAsync()).Path;
            else if (result.PrepareNotificationFile is { } prepare) path = (temporary = await prepare()).Path;
            else path = result.NotificationPath ?? throw new IOException("通知から開く画像・動画を準備できませんでした。");
            if (!disposed) open(path);
        }
        catch (Exception ex) { Warn("通知から外部アプリを開けません: " + ex.Message); }
        finally
        {
            image?.Dispose();
            if (temporary is not null) { temporary.Dispose(); retainDragFile?.Invoke(temporary.Path); }
        }
    }

    private void Warn(string text)
    {
        if (disposed) return;
        lastWarning = text;
        // Feedback failure must never turn a successful delivery into a failed one.
        try { warning?.Invoke(text); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        HideForCapture(); disposed = true;
        timer.Elapsed -= PollLifetime;
        timer.Dispose(); toast?.Dispose(); sound.Dispose();
    }

    private sealed class FeedbackTimer : IFeedbackTimer
    {
        private readonly System.Windows.Forms.Timer timer = new();
        public event Action? Elapsed;
        internal FeedbackTimer() => timer.Tick += (_, _) => { timer.Stop(); Elapsed?.Invoke(); };
        public void Restart(int milliseconds) { timer.Stop(); timer.Interval = milliseconds; timer.Start(); }
        public void Stop() => timer.Stop();
        public void Dispose() => timer.Dispose();
    }
}
