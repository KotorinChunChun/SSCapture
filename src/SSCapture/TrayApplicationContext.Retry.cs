namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private async Task RetryOutputAsync()
    {
        if (busy || exiting || pendingRecording is not null || recorder is not null) return;
        busy = true; RefreshMenu();
        try { StoreStatus(await output.RetryFailedAsync()); }
        catch (Exception error) { StoreStatus("出力を再実行できませんでした: " + error.Message, true); }
        finally
        {
            busy = false;
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
    }
}
