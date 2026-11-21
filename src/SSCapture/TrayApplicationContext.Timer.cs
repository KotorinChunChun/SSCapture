using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private const string TimerUiBusy = "撮影、録画、または名前入力を処理中です。";
    private CaptureTimerPump? timerReservation;
    private bool timerUiWasBusy;
    private readonly ToolStripMenuItem timerReserveItem = new("予約する");
    private readonly ToolStripMenuItem timerNextItem = new() { Enabled = false };
    private readonly ToolStripMenuItem timerStopItem = new("停止");

    private ToolStripMenuItem CreateTimerMenu()
    {
        var item = new ToolStripMenuItem("タイマー撮影");
        item.DropDownItems.AddRange([timerReserveItem, timerNextItem, timerStopItem]);
        item.DropDownOpening += (_, _) => RefreshTimerMenu();
        timerReserveItem.Click += (_, _) => Post(() => _ = ReserveTimerAsync());
        timerStopItem.Click += (_, _) =>
        {
            timerReservation?.Stop();
            RefreshTimerMenu();
        };
        return item;
    }

    private bool CanTakeTimerShot => !exiting && !busy && !recordingTransition && recorder is null
        && pendingRecording is null && namingForm is null;

    private void RefreshTimerAvailability()
    {
        bool unavailable = !CanTakeTimerShot;
        if (unavailable || timerUiWasBusy)
            timerReservation?.SkipDueWhileUnavailable(TimerUiBusy);
        timerUiWasBusy = unavailable;
        RefreshTimerMenu();
    }

    private void RefreshTimerMenu()
    {
        timerReserveItem.Enabled = timerReservation is null && CanTakeTimerShot;
        timerStopItem.Enabled = timerReservation is not null;
        timerNextItem.Text = timerReservation?.NextDue is DateTimeOffset next
            ? $"次回予定 {next.ToLocalTime():yyyy/MM/dd HH:mm:ss}"
            : timerReservation is null ? "予約なし" : "次回予定なし";
    }

    private async Task ReserveTimerAsync()
    {
        if (timerReservation is not null || !CanTakeTimerShot) return;
        CaptureTimerShot? shot;
        CaptureSchedule schedule;
        busy = true;
        RefreshMenu();
        try
        {
            var orderedBounds = InstantCapture.ReadDisplays().Select(display => display.Bounds).ToArray();
            var displays = RegionCoordinates.ReadDisplays().OrderBy(display => Array.IndexOf(orderedBounds, display.Bounds)).ToArray();
            using var dialog = new CaptureTimerForm(settings, displays);
            if (dialog.ShowDialog() != DialogResult.OK || dialog.Setup is not { } setup) return;
            schedule = setup.Command.Request.Schedule!;
            if (setup.Display is { } display)
                shot = CaptureTimerShot.Monitor(display, setup.Command.Settings, displays);
            else if (setup.SelectionMode is { } mode)
            {
                PrepareCaptureUi();
                shot = await CaptureCommandRunner.SelectTimerTargetAsync(setup.Command, mode: mode);
            }
            else
                shot = CaptureTimerShot.Registered(setup.Command.InputPreset, setup.Command.Settings);
        }
        catch (Exception ex)
        {
            NotifyTimerFailure(ex);
            return;
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
        if (shot is null || exiting) return;

        try
        {
            using var reservation = new CaptureTimerPump(schedule, capturedAt => TakeTimerShotAsync(shot, capturedAt),
                () => CanTakeTimerShot ? null : TimerUiBusy);
            timerReservation = reservation;
            RefreshTimerMenu();
            await reservation.RunAsync();
        }
        catch (Exception ex) { NotifyTimerFailure(ex); }
        finally
        {
            timerReservation = null;
            if (!exiting) RefreshTimerMenu();
        }
    }

    private async Task TakeTimerShotAsync(CaptureTimerShot shot, DateTimeOffset capturedAt)
    {
        busy = true;
        try
        {
            RefreshMenu();
            PrepareCaptureUi();
            StoreStatus(await shot.DeliverAsync(output, capturedAt));
        }
        finally
        {
            busy = false;
            RestoreCaptureSettings();
            if (!exiting) { RefreshMenu(); FlushFeedback(); }
        }
    }

    private void NotifyTimerFailure(Exception error)
    {
        if (exiting) return;
        string message = "タイマー撮影を終了しました。" + Environment.NewLine + error.Message;
        StoreStatus(message, true);
        if (settings.ShowCaptureNotification)
            tray.ShowBalloonTip(5000, "タイマー撮影", message, ToolTipIcon.Warning);
    }
}
