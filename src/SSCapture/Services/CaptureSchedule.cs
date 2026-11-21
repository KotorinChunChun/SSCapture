namespace SSCapture.Services;

internal abstract record CaptureSchedule
{
    internal sealed record Delay(int Seconds) : CaptureSchedule;
    internal sealed record At(DateTimeOffset When) : CaptureSchedule;
    internal sealed record Periodic(int IntervalSeconds, int? Count) : CaptureSchedule;

    internal void Validate(DateTimeOffset now)
    {
        if (this is Delay { Seconds: <= 0 }) throw new ArgumentException("待ち時間は1秒以上にしてください。");
        if (this is At at && at.When <= now) throw new ArgumentException("現在より後の日時を指定してください。");
        if (this is Periodic { IntervalSeconds: <= 0 }) throw new ArgumentException("撮影間隔は1秒以上にしてください。");
        if (this is Periodic { Count: <= 0 }) throw new ArgumentException("撮影枚数は1枚以上にしてください。");
    }
}

// 呼び出しと撮影完了を同じスレッドで処理する。停止時も進行中の出力が終わるまで待つ。
internal sealed class CaptureReservation
{
    private static readonly TimeSpan MaximumPollGap = TimeSpan.FromSeconds(1);
    private readonly TimeProvider clock;
    private readonly CaptureSchedule schedule;
    private readonly long start;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<DateTimeOffset, Task> capture;
    private readonly TimeSpan interval;
    private long lastPoll;
    private TimeSpan? nextElapsed;
    private DateTimeOffset? absolute;
    private bool capturing, stopping;
    private int successes;

    internal CaptureReservation(CaptureSchedule schedule, TimeProvider clock, Func<DateTimeOffset, Task> capture)
    {
        schedule.Validate(clock.GetUtcNow());
        this.schedule = schedule;
        this.clock = clock;
        this.capture = capture;
        start = lastPoll = clock.GetTimestamp();
        switch (schedule)
        {
            case CaptureSchedule.Delay delay: nextElapsed = TimeSpan.FromSeconds(delay.Seconds); break;
            case CaptureSchedule.At at: absolute = at.When; break;
            case CaptureSchedule.Periodic periodic:
                nextElapsed = TimeSpan.Zero;
                interval = TimeSpan.FromSeconds(periodic.IntervalSeconds);
                break;
        }
    }

    internal Task Completion => completion.Task;
    internal DateTimeOffset? NextDue
    {
        get
        {
            if (stopping) return null;
            if (absolute.HasValue) return absolute;
            return nextElapsed is TimeSpan next ? clock.GetUtcNow() + (next - clock.GetElapsedTime(start)) : null;
        }
    }

    internal void Stop()
    {
        stopping = true;
        if (!capturing) completion.TrySetResult();
    }

    internal void Poll(Func<string?> unavailable, bool resumed = false)
    {
        if (stopping) return;
        long timestamp = clock.GetTimestamp();
        bool interrupted = resumed || clock.GetElapsedTime(lastPoll, timestamp) > MaximumPollGap;
        lastPoll = timestamp;
        DateTimeOffset now = clock.GetLocalNow();
        TimeSpan elapsed = clock.GetElapsedTime(start, timestamp);
        if (nextElapsed is TimeSpan next ? elapsed < next : absolute is null || now < absolute.Value)
            return;

        string? reason = interrupted ? "スリープまたは処理の中断中に予定時刻を過ぎました。"
            : capturing ? "先行する撮影を処理中です。" : unavailable();
        if (schedule is CaptureSchedule.Periodic)
        {
            // 単調時計の開始点に揃え、経過した予定をまとめて実行しない。
            long remainder = elapsed.Ticks % interval.Ticks;
            nextElapsed = elapsed + TimeSpan.FromTicks(interval.Ticks - remainder);
        }
        else
        {
            nextElapsed = null;
            absolute = null;
        }

        if (reason is null)
        {
            capturing = true;
            _ = CaptureAsync(now, unavailable);
        }
        else if (schedule is not CaptureSchedule.Periodic)
        {
            stopping = true;
            completion.TrySetException(new InvalidOperationException(reason));
        }
    }

    private async Task CaptureAsync(DateTimeOffset capturedAt, Func<string?> unavailable)
    {
        try
        {
            await capture(capturedAt);
            if (schedule is not CaptureSchedule.Periodic periodic || periodic.Count is int count && ++successes >= count)
                stopping = true;
            // 完了前に通過した予定を先行処理中として消費する。
            if (!stopping) Poll(unavailable);
            capturing = false;
            if (stopping) completion.TrySetResult();
        }
        catch (Exception ex)
        {
            capturing = false;
            stopping = true;
            completion.TrySetException(ex);
        }
    }
}
