using System.Runtime.InteropServices;
using System.Text;

namespace SSCapture.Services;

internal sealed record ControlIdentity(string AutomationId, int Type);
internal sealed record ControlLookup(long Window, uint ProcessId, PixelBounds WindowBounds, PixelPoint? Point, ControlIdentity? Identity = null);
internal sealed record ControlHit(PixelBounds Bounds, ControlIdentity? Identity);

internal static partial class CaptureContextWorker
{
    private static ControlHit? LookupControl(IAutomation automation, ControlLookup lookup)
    {
        nint handle = (nint)lookup.Window;
        if (GetWindowThreadProcessId(handle, out uint pid) == 0 || pid != lookup.ProcessId) return null;
        PixelBounds viewport = lookup.WindowBounds;
        Rectangle window = new(viewport.X, viewport.Y, viewport.Width, viewport.Height);
        Point? pointer = lookup.Point is { } p ? new Point(p.X, p.Y) : null;
        if (pointer is Point point && !window.Contains(point)) return null;
        var className = new StringBuilder(128);
        GetClassName(handle, className, className.Capacity);
        string windowClass = className.ToString();
        bool browser = windowClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || windowClass == "MozillaWindowClass";
        IElement? root = null;
        try
        {
            root = automation.FromHandle(handle);
            if (root is not null)
            {
                // Chromium exposes document layout and text through the accessibility provider.
                // Raw View retains element and frame boundaries that Control View can omit.
                if (browser) return Search(root, automation.RawViewWalker(), 1200, 48);
                if (Search(root, automation.ControlViewWalker(), 220, 24) is { } hit) return hit;
            }

            ControlHit? Search(IElement start, ITreeWalker walker, int remaining, int depthLimit)
            {
                int matches = 0, bestDepth = -1;
                ControlHit? best = null;
                bool bestInteractive = false;
                try
                {
                    VisitChildren(start, 0);
                    return lookup.Identity is not null && (remaining <= 0 || matches != 1) ? null : best;
                }
                finally { Marshal.ReleaseComObject(walker); }

                void VisitChildren(IElement parent, int depth)
                {
                    if (remaining <= 0 || matches >= 2) return;
                    if (depth > depthLimit) { if (lookup.Identity is not null) remaining = 0; return; }
                    IElement? child = walker.FirstChild(parent);
                    try
                    {
                        while (child is not null && remaining-- > 0)
                        {
                            bool visible = child.Property(30022) is not true;
                            Rectangle bounds = visible ? ReadControlBounds(child, window) : Rectangle.Empty;
                            if (visible && (pointer is null || bounds.IsEmpty || bounds.Contains(pointer.Value)))
                            {
                                int type = child.Property(30003) is int controlType ? controlType : 0;
                                bool interactive = IsInteractiveControl(type);
                                bool container = type == 50032 /* Window */ ||
                                    browser && type is 0 or 50025 /* Custom */ or 50026 /* Group */ or 50030 /* Document */ or 50033 /* Pane */;
                                if (!bounds.IsEmpty && (interactive || bounds != window) && !container)
                                {
                                    string? id = child.Property(30011) as string;
                                    ControlIdentity? identity = string.IsNullOrEmpty(id) || id.Length > 160 ? null : new(id, type);
                                    if (lookup.Identity is { } wanted)
                                    {
                                        if (identity == wanted && ++matches == 1) best = new(PixelBounds.From(bounds), identity);
                                    }
                                    else if ((interactive && !bestInteractive) || interactive == bestInteractive && depth > bestDepth)
                                    {
                                        best = new(PixelBounds.From(bounds), identity);
                                        bestInteractive = interactive;
                                        bestDepth = depth;
                                    }
                                }
                                VisitChildren(child, depth + 1);
                            }
                            if (remaining <= 0 || matches >= 2) break;
                            IElement? next = walker.NextSibling(child);
                            Marshal.ReleaseComObject(child);
                            child = next;
                        }
                    }
                    finally { if (child is not null) Marshal.ReleaseComObject(child); }
                }
            }
        }
        catch (COMException) { }
        finally
        {
            if (root is not null) Marshal.ReleaseComObject(root);
        }
        return !browser && pointer is Point location && lookup.Identity is null ? NativeChild(handle, window, location) : null;
    }

    private static Rectangle ReadControlBounds(IElement control, Rectangle window)
    {
        if (control.Property(30001) is not double[] { Length: 4 } values
            || values.Any(value => !double.IsFinite(value) || value <= int.MinValue || value >= int.MaxValue)
            || values[2] <= 0 || values[3] <= 0) return Rectangle.Empty;
        int left = (int)Math.Max(window.Left, Math.Floor(values[0]));
        int top = (int)Math.Max(window.Top, Math.Floor(values[1]));
        int right = (int)Math.Min(window.Right, Math.Ceiling(values[0] + values[2]));
        int bottom = (int)Math.Min(window.Bottom, Math.Ceiling(values[1] + values[3]));
        return right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
    }

    internal static bool IsInteractiveControl(int type) => type is
        50000 or 50002 or 50003 or 50004 or 50005 or 50006 or 50007 or 50011 or 50013 or
        50015 or 50016 or 50018 or 50019 or 50023 or 50024 or 50025 or 50031 or 50035;

    private static ControlHit? NativeChild(nint root, Rectangle window, Point pointer)
    {
        nint current = root;
        for (int depth = 0; depth < 24; depth++)
        {
            var local = new NativePoint(pointer.X, pointer.Y);
            if (!ScreenToClient(current, ref local)) break;
            // Skip invisible, disabled and transparent children.
            nint child = ChildWindowFromPointEx(current, local, 7);
            if (child == 0 || child == current) break;
            current = child;
        }
        if (current == root || !GetWindowRect(current, out NativeRect native)) return null;
        Rectangle bounds = Rectangle.Intersect(window, Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom));
        return bounds.Contains(pointer) && bounds != window ? new(PixelBounds.From(bounds), null) : null;
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITreeWalker
    {
        IElement? Parent([MarshalAs(UnmanagedType.Interface)] IElement element);
        IElement? FirstChild([MarshalAs(UnmanagedType.Interface)] IElement element);
        IElement? LastChild([MarshalAs(UnmanagedType.Interface)] IElement element);
        IElement? NextSibling([MarshalAs(UnmanagedType.Interface)] IElement element);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint window, NativePoint point, uint flags);
}
