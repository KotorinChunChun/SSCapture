namespace SSCapture.Services;

internal sealed partial class OutputRouter
{
    private readonly object retryGate = new();
    private RetryJob? pendingRetry;
    private long retrySerial;
    private bool retrying, retryDisposed;
    internal bool HasPendingRetry { get { lock (retryGate) return pendingRetry is not null && !retrying; } }
    internal string? RetryNotice { get; private set; }

    private sealed class RetryJob(Bitmap image, long serial) : IDisposable
    {
        internal Bitmap Image { get; } = image;
        internal long Serial { get; } = serial;
        internal OutputDestination Pending;
        internal bool FolderPending, IsRetry, SuccessNotified;
        internal string? SavedPath, ExternalPath;
        internal Func<Task<bool?>>? PrepareFile;
        internal Func<Task<string>> Execute { get; set; } = null!;
        internal bool Failed => Pending != OutputDestination.None || FolderPending;
        public void Dispose() => Image.Dispose();
    }

    private RetryJob NewRetry(Bitmap image)
    {
        RetryJob job;
        lock (retryGate)
        {
            if (retryDisposed) { image.Dispose(); throw new ObjectDisposedException(nameof(OutputRouter)); }
            pendingRetry?.Dispose(); pendingRetry = null;
            RetryNotice = null;
            job = new RetryJob(image, ++retrySerial);
        }
        RetryChanged();
        return job;
    }

    internal async Task<string> RetryFailedAsync()
    {
        RetryJob job;
        lock (retryGate)
        {
            if (retryDisposed) throw new ObjectDisposedException(nameof(OutputRouter));
            if (retrying) throw new InvalidOperationException("出力の再実行中です。");
            job = pendingRetry ?? throw new InvalidOperationException("再実行できる出力はありません。");
            pendingRetry = null; retrying = true; job.IsRetry = true;
        }
        RetryChanged();
        try { return await ExecuteRetryJobAsync(job); }
        finally { lock (retryGate) retrying = false; RetryChanged(); }
    }

    private async Task<string> ExecuteRetryJobAsync(RetryJob job)
    {
        try { return await job.Execute(); }
        finally
        {
            bool retained = false;
            lock (retryGate)
            {
                if (!retryDisposed && job.Serial == retrySerial && job.Failed)
                {
                    long bytes = (long)job.Image.Width * job.Image.Height * 4;
                    if (bytes <= retryByteLimit)
                    {
                        pendingRetry?.Dispose(); pendingRetry = job; retained = true;
                        RetryNotice = "トレイの「失敗した出力を再実行」からやり直せます。";
                    }
                    else RetryNotice = "再実行用画像の保持上限を超えたため、撮り直しが必要です。";
                }
                else if (job.Serial == retrySerial) RetryNotice = null;
            }
            if (!retained) job.Dispose();
            RetryChanged();
        }
    }

    private void RetryChanged()
    {
        try { retryChanged?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
    }

    public void Dispose()
    {
        lock (retryGate)
        {
            retryDisposed = true;
            pendingRetry?.Dispose(); pendingRetry = null;
        }
    }
}
