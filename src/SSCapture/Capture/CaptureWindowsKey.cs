using System.Runtime.InteropServices;

namespace SSCapture.Capture;

// Only active in an interactive capture session. All keys other than Win pass through.
internal sealed class CaptureWindowsKey : IDisposable
{
    private readonly HookProcedure callback;
    private nint hook;
    private int held;
    private int previouslyHeld;
    private bool combined;
    private bool tapped;

    internal bool IsHeld => held != 0;
    internal bool TakeTap()
    {
        bool value = tapped;
        tapped = false;
        return value;
    }

    internal CaptureWindowsKey()
    {
        previouslyHeld = (CaptureNative.IsKeyDown(0x5b) ? 1 : 0) | (CaptureNative.IsKeyDown(0x5c) ? 2 : 0);
        callback = OnKey;
        hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint OnKey(int code, nint message, nint data)
    {
        if (code == 0)
        {
            int key = Marshal.ReadInt32(data);
            int side = key == 0x5b ? 1 : key == 0x5c ? 2 : 0;
            bool down = message.ToInt32() is 0x100 or 0x104;
            if ((previouslyHeld & side) != 0)
            {
                if (!down) previouslyHeld &= ~side;
                return CallNextHookEx(0, code, message, data);
            }
            if (side != 0 && (held & side) != 0 && !down)
            {
                held &= ~side;
                if (held == 0 && !combined) tapped = true;
                return 1;
            }
            if (side != 0 && down && CaptureNative.OwnsForegroundWindow())
            {
                if (held == 0) combined = CaptureNative.Modifiers() != Keys.None;
                else if ((held & side) == 0) combined = true;
                held |= side;
                return 1;
            }
            if (side == 0 && down && held != 0) combined = true;
        }
        return CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (hook != 0) UnhookWindowsHookEx(hook);
        hook = 0;
        GC.KeepAlive(callback);
    }

    private delegate nint HookProcedure(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int type, HookProcedure callback, nint module, uint threadId);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
}
