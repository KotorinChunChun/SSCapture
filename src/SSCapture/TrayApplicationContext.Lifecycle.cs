using System.Diagnostics;
using Microsoft.Win32;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    protected override void ExitThreadCore()
    {
        if (OperationPolicy.MustWaitToStop)
        {
            Report("撮影・録画を完了またはキャンセルしてから終了してください。", true);
            if (recordingForm is not null)
                WindowVisibility.Show(recordingForm);
            return;
        }

        CancelRecording();
        exiting = true;
        feedback.HideForCapture();
        deferredFeedback?.NotificationImage?.Dispose(); deferredFeedback = null;
        hook?.Dispose();
        hook = null;
        tray.Visible = false;
        form?.Close();
        historyForm?.Close();
        pinnedImages.CloseAll();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            exiting = true;
            management.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            signalWait.Unregister(null);
            settingsSignal.Dispose();
            hook?.Dispose();
            tray.Dispose();
            icon.Dispose();
            menu.Dispose();
            form?.Dispose();
            recordingForm?.Dispose();
            dispatcher.Dispose();
            historyForm?.Dispose();
            history.Dispose();
            output.Dispose();
            pinnedImages.Dispose();
            CaptureContextClient.Shared.Dispose();
            feedback.Dispose();
        }

        base.Dispose(disposing);
    }
}
