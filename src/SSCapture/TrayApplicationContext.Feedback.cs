using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void NotifyDelivery(CaptureDelivery result)
    {
        if (exiting)
            return;
        if (OperationPolicy.MustDeferFeedback)
        {
            deferredFeedback = result;
            return;
        }

        feedback.Notify(result, settings);
    }

    private void DispatchDelivery(CaptureDelivery result)
    {
        if (exiting || dispatcher.IsDisposed)
            return;
        if (dispatcher.InvokeRequired)
            Post(() => NotifyDelivery(result));
        else
            NotifyDelivery(result);
    }

    private void FlushFeedback()
    {
        if (exiting || OperationPolicy.MustDeferFeedback || deferredFeedback is null)
            return;
        var result = deferredFeedback;
        deferredFeedback = null;
        feedback.Notify(result, settings);
    }

    private void StoreStatus(string text, bool error = false)
    {
        if (exiting)
            return;
        lastStatus = new StatusMessage(text, error, DateTimeOffset.Now);
        form?.SetStatus(lastStatus);
    }

    private void Report(string text, bool error)
    {
        if (exiting)
            return;
        StoreStatus(text, error);
        if (error)
        {
            bool wasBusy = busy;
            busy = false;
            ShowSettings();
            busy = wasBusy;
        }
    }
}
