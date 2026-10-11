using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal sealed class KeyboardHook : IDisposable
{
    private static readonly (int Key, KeyboardModifiers Modifier)[] ModifierKeys =
    [
        (0x5B, KeyboardModifiers.LeftWindows), (0x5C, KeyboardModifiers.RightWindows),
        (0xA0, KeyboardModifiers.LeftShift), (0xA1, KeyboardModifiers.RightShift),
        (0xA2, KeyboardModifiers.LeftControl), (0xA3, KeyboardModifiers.RightControl),
        (0xA4, KeyboardModifiers.LeftAlt), (0xA5, KeyboardModifiers.RightAlt)
    ];
    private readonly Thread thread;
    private readonly HookProc callback;
    private readonly WaitCallback dispatchCapture;
    private readonly HotkeyAssignment[] assignments;
    private readonly ManualResetEventSlim initialized = new(false);
    private HotkeyState state = new();
    private Exception? startupError;
    private uint threadId;
    private nint hook;
    private int stopping;
    private int enabled = 1;

    public bool Enabled { get => Volatile.Read(ref enabled) != 0; set => Volatile.Write(ref enabled, value ? 1 : 0); }

    public KeyboardHook(Action onCapture) : this(WrapLegacyCallback(onCapture), new AppSettings
    {
        ScreenshotShortcut1 = "Win+Shift+S", ScreenshotShortcut2 = "",
        RecordingShortcut1 = "", RecordingShortcut2 = "", InstantShortcuts = Enumerable.Repeat("", 10).ToArray(), LastRegionShortcut = ""
    }) { }

    public KeyboardHook(Action<CaptureIntent> onCapture, AppSettings settings) : this(onCapture, settings, true) { }

    public KeyboardHook(Action<CaptureIntent> onCapture, AppSettings settings, bool initiallyEnabled, Action<int>? onInstantCapture = null, Action<string>? onRegionCapture = null)
    {
        ArgumentNullException.ThrowIfNull(onCapture);
        ArgumentNullException.ThrowIfNull(settings);
        assignments = HotkeyBinding.ReadAssignments(settings);
        Enabled = initiallyEnabled && assignments.Length > 0;
        callback = HandleKeyboard;
        dispatchCapture = intent =>
        {
            if (Volatile.Read(ref stopping) != 0 || !Enabled) return;
            // A shutdown race (for example, a disposed UI handle) must not terminate
            // the process from a native callback or ThreadPool work item.
            try
            {
                var decision = (HotkeyDecision)intent!;
                if (decision.RegionId is { } id) onRegionCapture?.Invoke(id);
                else if (decision.DisplayNumber > 0) onInstantCapture?.Invoke(decision.DisplayNumber);
                else onCapture(decision.Intent);
            }
            catch (Exception) { }
        };
        thread = new Thread(MessageLoop) { IsBackground = true, Name = "SSCapture keyboard hook" };
        thread.Start();
        if (!initialized.Wait(TimeSpan.FromSeconds(10)))
        {
            Dispose();
            throw new TimeoutException("ショートカット監視の開始がタイムアウトしました。");
        }
        if (startupError is not null)
        {
            Dispose();
            throw new InvalidOperationException("ショートカット監視を開始できませんでした。", startupError);
        }
    }

    private static Action<CaptureIntent> WrapLegacyCallback(Action onCapture)
    {
        ArgumentNullException.ThrowIfNull(onCapture);
        return _ => onCapture();
    }

    private void MessageLoop()
    {
        try
        {
            threadId = GetCurrentThreadId();
            // Create the queue before exposing threadId to the disposing caller.
            PeekMessage(out _, 0, 0, 0, 0);
            int[] held = assignments.Select(item => item.Binding.VirtualKey).Distinct()
                .Where(key => (GetAsyncKeyState(key) & 0x8000) != 0).ToArray();
            state = new HotkeyState(assignments, ReadModifiers(), held);
            if (Volatile.Read(ref stopping) != 0) return;
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized.Set();
            while (Volatile.Read(ref stopping) == 0 && GetMessage(out Message message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { startupError = ex; }
        finally
        {
            if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
            initialized.Set();
        }
    }

    private nint HandleKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && Volatile.Read(ref stopping) == 0)
        {
            int kind = (int)message;
            bool isDown = kind is 0x0100 or 0x0104;
            bool isUp = kind is 0x0101 or 0x0105;
            if (isDown || isUp)
            {
                int virtualKey = Marshal.ReadInt32(data);
                // A lock/unlock or an earlier hook may hide modifier releases. The
                // current event is a target key, so prior modifier state is valid.
                if (state.IsTargetKey(virtualKey)) state.SynchronizeModifiers(ReadModifiers());
                HotkeyDecision decision = state.Process(virtualKey, isDown, Enabled);
                if (decision.Capture) ThreadPool.QueueUserWorkItem(dispatchCapture, decision);
                if (decision.Suppress) return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static KeyboardModifiers ReadModifiers()
    {
        KeyboardModifiers result = KeyboardModifiers.None;
        foreach (var (key, modifier) in ModifierKeys)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) result |= modifier;
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        if (threadId != 0) PostThreadMessage(threadId, 0x0012, 0, 0);
        if (Thread.CurrentThread != thread && thread.IsAlive) thread.Join(TimeSpan.FromSeconds(2));
        // Keep the startup event alive if a timed-out startup thread is still unwinding.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private; }
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
