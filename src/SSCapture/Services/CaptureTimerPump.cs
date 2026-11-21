using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SSCapture.Services;

// WinFormsのスレッドを保ったまま、CLIと常駐の予約を駆動する。
internal sealed class CaptureTimerPump : IDisposable
{
    private readonly CaptureReservation reservation;
    private readonly Func<string?> unavailable;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private int suspended, sessionUnavailable, resumed;

    internal CaptureTimerPump(CaptureSchedule schedule, Func<DateTimeOffset, Task> capture, Func<string?>? unavailable = null)
    {
        reservation = new(schedule, TimeProvider.System, capture);
        this.unavailable = () => Volatile.Read(ref suspended) != 0 || Volatile.Read(ref sessionUnavailable) != 0 || !IsInputDesktop()
            ? "Windowsのユーザーセッションで撮影できない状態です。" : unavailable?.Invoke();
        timer.Tick += (_, _) => Poll();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal DateTimeOffset? NextDue => reservation.NextDue;

    internal Task RunAsync()
    {
        timer.Start();
        Poll();
        return reservation.Completion;
    }

    private void Poll() => reservation.Poll(unavailable, Interlocked.Exchange(ref resumed, 0) != 0);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Volatile.Write(ref suspended, 1);
        if (e.Mode == PowerModes.Resume)
        {
            Volatile.Write(ref resumed, 1);
            Volatile.Write(ref suspended, 0);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Volatile.Write(ref sessionUnavailable, 1);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                Volatile.Write(ref resumed, 1);
                Volatile.Write(ref sessionUnavailable, 0);
                break;
        }
    }

    internal void SkipDueWhileUnavailable(string reason) => reservation.Poll(() => reason);
    internal void Stop() => reservation.Stop();

    public void Dispose()
    {
        Stop();
        timer.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private static bool IsInputDesktop()
    {
        const uint readObjects = 1;
        const int nameIndex = 2;
        nint desktop = OpenInputDesktop(0, false, readObjects);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, nameIndex, name, name.Capacity * sizeof(char), out _)
                && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder buffer, int length, out int needed);
}
