using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SSCapture.Capture;

internal sealed class CapturePresentation : NativeWindow, IDisposable
{
    private readonly Rectangle bounds;
    private nint memoryDc;
    private nint dib;
    private nint oldBitmap;
    private Bitmap? bitmap;
    private Graphics? drawing;
    internal bool Visible { get; private set; }

    internal CapturePresentation(Rectangle bounds)
    {
        this.bounds = bounds;
        CreateHandle(new CreateParams
        {
            Caption = WindowTitle.Create("撮影操作の共有"),
            X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = CaptureNative.WsExToolWindow | 0x00080000 | 0x00000020 | 0x08000000 | 0x8
        });
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) { message.Result = -1; return; } // HTTRANSPARENT
        base.WndProc(ref message);
    }

    internal unsafe bool Present(Rectangle dirty, Action<Graphics, Rectangle, bool> paint)
    {
        if (drawing is null && !CreateSurface()) return false;
        Graphics graphics = drawing!;
        if (!Visible) dirty = new Rectangle(Point.Empty, bounds.Size);
        if (dirty.Width <= 0 || dirty.Height <= 0) return Visible;
        graphics.SetClip(dirty);
        try
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.FillRectangle(Brushes.Transparent, dirty);
            graphics.CompositingMode = CompositingMode.SourceOver;
            paint(graphics, dirty, true);
        }
        finally { graphics.ResetClip(); }
        graphics.Flush();
        Point location = bounds.Location, source = Point.Empty;
        Size size = bounds.Size;
        var blend = new Blend { Alpha = 255, AlphaFormat = 1 };
        var area = new CaptureNative.NativeRect { Left = dirty.Left, Top = dirty.Top, Right = dirty.Right, Bottom = dirty.Bottom };
        var update = new LayeredUpdate
        {
            SizeOf = (uint)sizeof(LayeredUpdate),
            Location = &location, SurfaceSize = &size, SourceDc = memoryDc, Source = &source,
            Blend = &blend, Flags = 2 /* ULW_ALPHA */, Dirty = &area
        };
        if (!UpdateLayeredWindowIndirect(Handle, ref update)) return false;
        if (!Visible)
        {
            // Avoid WinForms' active-window bookkeeping for a purely visual layer.
            if (!CaptureNative.SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0053)) return false;
            Visible = true;
        }
        return true;
    }

    private bool CreateSurface()
    {
        memoryDc = CreateCompatibleDC(0);
        if (memoryDc == 0) return false;
        var info = new BitmapHeader { Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32 };
        dib = CreateDIBSection(memoryDc, ref info, 0, out nint pixels, 0, 0);
        if (dib != 0 && pixels != 0) oldBitmap = SelectObject(memoryDc, dib);
        if (oldBitmap == 0 || oldBitmap == -1) { ReleaseSurface(); return false; }
        bitmap = new Bitmap(bounds.Width, bounds.Height, checked(bounds.Width * 4), PixelFormat.Format32bppPArgb, pixels);
        drawing = Graphics.FromImage(bitmap);
        drawing.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        return true;
    }

    private void ReleaseSurface()
    {
        drawing?.Dispose();
        bitmap?.Dispose();
        if (oldBitmap != 0 && oldBitmap != -1) _ = SelectObject(memoryDc, oldBitmap);
        if (dib != 0) _ = DeleteObject(dib);
        if (memoryDc != 0) _ = DeleteDC(memoryDc);
        drawing = null;
        bitmap = null;
        oldBitmap = dib = memoryDc = 0;
    }

    internal void Hide()
    {
        if (!Visible) return;
        _ = ShowWindow(Handle, 0);
        Visible = false;
    }

    public void Dispose()
    {
        Hide();
        ReleaseSurface();
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend { public byte Operation, Flags, Alpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct LayeredUpdate
    {
        public uint SizeOf;
        public nint TargetDc;
        public Point* Location;
        public Size* SurfaceSize;
        public nint SourceDc;
        public Point* Source;
        public uint ColorKey;
        public Blend* Blend;
        public uint Flags;
        public CaptureNative.NativeRect* Dirty;
    }

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info,
        uint colorUsage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindowIndirect(nint window, ref LayeredUpdate update);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
