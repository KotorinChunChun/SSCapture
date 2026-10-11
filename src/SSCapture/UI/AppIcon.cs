using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SSCapture.UI;

internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(0, 104, 116));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var pen = new Pen(Color.White, 2.5f);
        graphics.DrawRectangle(pen, 8, 9, 16, 14);
        graphics.DrawLine(pen, 5, 6, 11, 6);
        graphics.DrawLine(pen, 6, 5, 6, 11);
        graphics.DrawLine(pen, 21, 26, 27, 26);
        graphics.DrawLine(pen, 26, 21, 26, 27);
        nint handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
