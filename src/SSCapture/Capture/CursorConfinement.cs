using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SSCapture.Capture;

internal sealed class CursorConfinement : IDisposable
{
    private readonly Rectangle? previous;
    private readonly Action<Rectangle?> apply;
    private bool disposed;

    internal CursorConfinement(Rectangle bounds, Rectangle? previous, Action<Rectangle?> apply)
    {
        this.previous = previous;
        this.apply = apply;
        apply(bounds);
    }

    internal static IDisposable Acquire(Rectangle bounds)
    {
        Rectangle current = ReadBounds();
        Rectangle desktop = CaptureNative.MonitorBounds().Aggregate(Rectangle.Union);
        return new CursorConfinement(bounds, current == desktop ? null : current, Apply);
    }

    internal static Rectangle ReadBounds()
    {
        if (!GetClipCursor(out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return rect.ToRectangle();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { apply(previous); }
        catch
        {
            // 保存した範囲の復元に失敗した場合も、今回の制限を残さないよう解除を試みる。
            apply(null);
            throw;
        }
    }

    private static void Apply(Rectangle? bounds)
    {
        bool success;
        if (bounds is Rectangle area)
        {
            var rect = new CaptureNative.NativeRect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
            success = ClipCursor(ref rect);
        }
        else success = ReleaseCursor(0);
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out CaptureNative.NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref CaptureNative.NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCursor(nint rect);
}
