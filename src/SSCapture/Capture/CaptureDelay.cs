using System.Diagnostics;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Capture;

internal enum CaptureDelayResult { Ready, Cancelled }

internal readonly record struct CaptureCountdownFrame(int Seconds, double Fraction)
{
    internal static CaptureCountdownFrame FromElapsed(TimeSpan duration, TimeSpan elapsed)
    {
        if (duration <= TimeSpan.Zero) return default;
        double remaining = Math.Clamp((duration - elapsed).TotalSeconds, 0, duration.TotalSeconds);
        return new((int)Math.Ceiling(remaining), remaining / duration.TotalSeconds);
    }

    internal CaptureDelayResult? Result(bool escape) => escape ? CaptureDelayResult.Cancelled
        : Seconds == 0 ? CaptureDelayResult.Ready : null;
}

internal static class CaptureDelay
{
    internal static string Label(CaptureStartDelay delay) => delay == CaptureStartDelay.None ? "待ち時間なし" : $"{(int)delay}秒遅延";

    internal static Task WaitAsync(CaptureStartDelay delay, Func<TimeSpan, Task>? wait = null) =>
        delay == CaptureStartDelay.None ? Task.CompletedTask : (wait ?? Task.Delay)(TimeSpan.FromSeconds((int)delay));

    internal static Task<CaptureDelayResult> WaitForSelectionAsync(CaptureStartDelay delay, CaptureOptions options)
    {
        Rectangle? target = options.InitialInputPreset is { UseFixedCoordinates: true } preset &&
            options.RestartState?.UseFixedCoordinates != false ? ResolveCountdownTarget(preset, RegionCoordinates.ReadDisplays) : null;
        return WaitInteractivelyAsync(delay, target);
    }

    internal static async Task<Rectangle?> WaitForRegionAsync(SavedRegion region, Func<IReadOnlyList<DisplayLayout>> readDisplays,
        Func<CaptureStartDelay, Rectangle?, Task<CaptureDelayResult>>? wait = null)
    {
        if (region.StartDelay != CaptureStartDelay.None)
        {
            Rectangle? target = ResolveCountdownTarget(region, readDisplays);
            if (await (wait ?? WaitInteractivelyAsync)(region.StartDelay, target) == CaptureDelayResult.Cancelled)
                return null;
        }
        // 待機後の画面構成で座標を解決する。
        return RegionCoordinates.Resolve(region, readDisplays());
    }

    private static Rectangle? ResolveCountdownTarget(SavedRegion region, Func<IReadOnlyList<DisplayLayout>> readDisplays)
    {
        try { return RegionCoordinates.Resolve(region, readDisplays()); }
        // 登録範囲のエラーは撮影開始時に扱い、待機表示はポインタのモニターに出す。
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    internal static async Task<CaptureDelayResult> WaitInteractivelyAsync(CaptureStartDelay delay, Rectangle? target)
    {
        if (delay == CaptureStartDelay.None) return CaptureDelayResult.Ready;

        var location = NotificationPlacement.Current(NotificationDisplay.Captured, target ??
            new Rectangle(CaptureNative.CursorPosition(), new Size(1, 1)));
        var duration = TimeSpan.FromSeconds((int)delay);
        var completed = new TaskCompletionSource<CaptureDelayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var countdown = new CaptureCountdownForm(location.WorkArea, location.Dpi))
        using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
        using (var keyboard = new CaptureWindowsKey(() => true, () => true, interceptWindowsKey: false))
        {
            long started = Stopwatch.GetTimestamp();
            countdown.FormClosed += OnClosed;
            countdown.UpdateFrame(CaptureCountdownFrame.FromElapsed(duration, TimeSpan.Zero));
            countdown.ShowCountdown();
            timer.Tick += (_, _) =>
            {
                var frame = CaptureCountdownFrame.FromElapsed(duration, Stopwatch.GetElapsedTime(started));
                if (frame.Result(keyboard.TakeEscape()) is { } result)
                {
                    timer.Stop();
                    keyboard.Dispose();
                    try
                    {
                        countdown.FormClosed -= OnClosed;
                        countdown.Close();
                        completed.TrySetResult(result);
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                }
                else countdown.UpdateFrame(frame);
            };
            timer.Start();
            if (await completed.Task == CaptureDelayResult.Cancelled) return CaptureDelayResult.Cancelled;

            void OnClosed(object? sender, FormClosedEventArgs e)
            {
                timer.Stop();
                keyboard.Dispose();
                completed.TrySetResult(CaptureDelayResult.Cancelled);
            }
        }
        CaptureNative.SynchronizeDesktop();
        return CaptureDelayResult.Ready;
    }
}
