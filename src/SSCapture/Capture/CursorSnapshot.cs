using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SSCapture.Capture;

/// <summary>Owns the cursor shape and physical position captured before selection UI appears.</summary>
internal sealed class CursorSnapshot : IDisposable
{
    private readonly SafeIconHandle? icon;
    private readonly Bitmap? image;
    private readonly Point screenPosition;
    private readonly Point hotspot;
    private readonly Size size;
    private bool disposed;

    private CursorSnapshot(SafeIconHandle icon, Point screenPosition, Point hotspot, Size size)
    {
        this.icon = icon;
        this.screenPosition = screenPosition;
        this.hotspot = hotspot;
        this.size = size;
    }

    private CursorSnapshot(Bitmap image, Point screenPosition, Point hotspot)
    {
        this.image = image;
        this.screenPosition = screenPosition;
        this.hotspot = hotspot;
        size = image.Size;
    }

    internal static CursorSnapshot? Capture()
    {
        NativeCursorInfo cursor = new() { Size = Marshal.SizeOf<NativeCursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || (cursor.Flags & 2) != 0 || cursor.Cursor == 0)
            return null;

        SafeIconHandle? ownedIcon = new(CopyIcon(cursor.Cursor));
        try
        {
            if (ownedIcon.IsInvalid || !GetPhysicalCursorPos(out NativePoint position)) return null;
            return FromOwnedIcon(ref ownedIcon, new Point(position.X, position.Y));
        }
        finally { ownedIcon?.Dispose(); }
    }

    private static CursorSnapshot? FromOwnedIcon(ref SafeIconHandle? ownedIcon, Point position)
    {
        NativeIconInfo information = default;
        try
        {
            if (!GetIconInfo(ownedIcon!, out information)) return null;
            nint bitmap = information.Color != 0 ? information.Color : information.Mask;
            if (bitmap == 0 || GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap dimensions) == 0)
                return null;
            int height = information.Color != 0 ? dimensions.Height : dimensions.Height / 2;
            if (dimensions.Width <= 0 || height <= 0 || information.HotspotX > int.MaxValue || information.HotspotY > int.MaxValue)
                return null;
            CursorSnapshot result = new(ownedIcon!, position,
                new Point((int)information.HotspotX, (int)information.HotspotY), new Size(dimensions.Width, height));
            ownedIcon = null;
            return result;
        }
        finally
        {
            // GetIconInfo allocates both bitmap handles independently of the copied cursor.
            if (information.Mask != 0) DeleteObject(information.Mask);
            if (information.Color != 0) DeleteObject(information.Color);
        }
    }

    /// <summary>Clones a synthetic cursor image without reading the screen or current pointer.</summary>
    internal static CursorSnapshot FromImage(Bitmap image, Point screenPosition, Point hotspot)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new CursorSnapshot((Bitmap)image.Clone(), screenPosition, hotspot);
    }

    /// <summary>Copies a supplied cursor/icon, allowing isolated native rendering checks.</summary>
    internal static CursorSnapshot? FromIcon(nint nativeIcon, Point screenPosition)
    {
        if (nativeIcon == 0) return null;
        SafeIconHandle? ownedIcon = new(CopyIcon(nativeIcon));
        try { return ownedIcon.IsInvalid ? null : FromOwnedIcon(ref ownedIcon, screenPosition); }
        finally { ownedIcon?.Dispose(); }
    }

    internal static Point DrawLocation(Point screenPosition, Point hotspot, Point imageScreenOrigin) =>
        new(screenPosition.X - hotspot.X - imageScreenOrigin.X,
            screenPosition.Y - hotspot.Y - imageScreenOrigin.Y);

    internal void Draw(Bitmap targetImage, Point imageScreenOrigin, Region? clip = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(targetImage);
        using Graphics graphics = Graphics.FromImage(targetImage);
        if (clip is not null) graphics.SetClip(clip, CombineMode.Intersect);
        Point location = DrawLocation(screenPosition, hotspot, imageScreenOrigin);
        Rectangle area = Rectangle.Intersect(new Rectangle(location, size), Rectangle.Ceiling(graphics.VisibleClipBounds));
        if (area.Width <= 0 || area.Height <= 0 || !graphics.IsVisible(area)) return;

        try
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            if (image is not null)
            {
                graphics.DrawImageUnscaled(image, location);
                return;
            }

            // A Graphics HDC backed by ARGB is write-only, so never read it with BitBlt.
            // Seed a native bitmap from actual frozen pixels before applying AND/XOR or
            // alpha cursor masks. Copy back through GDI+ to keep destination alpha valid.
            using Bitmap composite = new(area.Width, area.Height, PixelFormat.Format32bppRgb);
            using (Graphics background = Graphics.FromImage(composite))
                background.DrawImage(targetImage, new Rectangle(Point.Empty, area.Size), area, GraphicsUnit.Pixel);
            nint bitmap = composite.GetHbitmap();
            nint dc = CreateCompatibleDC(0);
            try
            {
                if (dc == 0) return;
                nint previous = SelectObject(dc, bitmap);
                if (previous == 0 || previous == new nint(-1)) return;
                try
                {
                    if (!DrawIconEx(dc, location.X - area.X, location.Y - area.Y,
                        icon!, 0, 0, 0, 0, 3 /* DI_NORMAL, native resource dimensions */)) return;
                }
                finally { SelectObject(dc, previous); }
                using Bitmap rendered = Image.FromHbitmap(bitmap);
                graphics.DrawImageUnscaled(rendered, area.Location);
            }
            finally
            {
                if (dc != 0) DeleteDC(dc);
                DeleteObject(bitmap);
            }
        }
        catch (ExternalException)
        {
            // An unavailable pointer/HDC must not discard an otherwise successful capture.
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        image?.Dispose();
        icon?.Dispose();
    }

    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeIconHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursorInfo
    {
        public int Size;
        public uint Flags;
        public nint Cursor;
        public NativePoint Position;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref NativeCursorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(SafeIconHandle icon, out NativeIconInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(nint bitmap, int size, out NativeBitmap information);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, SafeIconHandle icon,
        int width, int height, uint frame, nint background, uint flags);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);
}
