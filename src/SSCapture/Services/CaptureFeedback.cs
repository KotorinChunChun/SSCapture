using SSCapture.UI;

namespace SSCapture.Services;

internal enum NotificationCorner { BottomRight, BottomLeft }

internal interface ICaptureToast : IDisposable
{
    Rectangle DisplayBounds => Rectangle.Empty;
    int DisplayDpi => 96;
    bool TryShow(string message, bool hasFailure, NotificationCorner corner, out string? warning,
        Action? activate = null, NotificationDisplay display = NotificationDisplay.Captured, Rectangle? captureBounds = null);
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
    private int notificationSerial;
    private ICaptureToast? toast;
    private volatile bool disposed;
    private string? lastWarning;
    private readonly Func<long> now;
    private readonly Func<Point> pointer;
    private NotificationLifetime? lifetime;

    internal CaptureFeedback(Action<string>? warning = null, Func<ICaptureToast>? createToast = null,
        ICompletionSound? sound = null, IFeedbackTimer? timer = null, Action<string>? openSavedFile = null,
        Func<long>? now = null, Func<Point>? pointer = null)
    {
        this.warning = warning;
        this.createToast = createToast ?? (() => new CaptureToastForm());
        this.sound = sound ?? new CompletionSound(warning: Warn);
        this.timer = timer ?? new FeedbackTimer();
        this.openSavedFile = openSavedFile ?? (path => ExplorerSelection.Open(path));
        this.now = now ?? (() => Environment.TickCount64);
        this.pointer = pointer ?? Capture.CaptureNative.CursorPosition;
        this.timer.Elapsed += PollLifetime;
    }

    internal string? LastWarning => lastWarning ?? sound.LastWarning;

    internal void Notify(CaptureDelivery result, AppSettings settings)
    {
        if (disposed || (!result.HasSuccess && !result.HasFailure)) return;
        if (settings.PlayCaptureSound && result.HasSuccess)
        {
            try { sound.Request(); }
            catch (Exception ex) { Warn("効果音を再生できません: " + ex.Message); }
        }
        if (!settings.ShowCaptureNotification) { HideForCapture(); return; }
        try
        {
            int serial = ++notificationSerial;
            Action? activate = result.Completed.HasFlag(OutputDestination.File) && result.SavedPath is string path
                ? () => OpenSavedFile(serial, path) : null;
            toast ??= createToast();
            if (!toast.TryShow(result.Message, result.HasFailure, settings.NotificationCorner, out string? error,
                activate, settings.NotificationDisplay, result.CaptureBounds))
            {
                HideForCapture();
                Warn(error ?? "完了表示を画面取得から除外できないため、表示を中止しました。");
                return;
            }
            int duration = checked(settings.NotificationDurationSeconds * 1000);
            lifetime = new NotificationLifetime(this.now(), duration);
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
        notificationSerial++;
        lifetime = null;
        timer.Stop();
        toast?.Hide();
    }

    private void PollLifetime()
    {
        if (disposed || lifetime is null || toast is null) return;
        try
        {
            if (toast.DisplayBounds.IsEmpty || lifetime.Expired(now(), pointer(), toast.DisplayBounds, toast.DisplayDpi)) HideForCapture();
            else timer.Restart(50);
        }
        catch (Exception ex) { HideForCapture(); Warn("通知の近接状態を確認できません: " + ex.Message); }
    }

    private void OpenSavedFile(int serial, string path)
    {
        if (disposed || serial != notificationSerial) return;
        HideForCapture();
        try { openSavedFile(path); }
        catch (Exception ex) { Warn("保存ファイルをExplorerで表示できません: " + ex.Message); }
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
