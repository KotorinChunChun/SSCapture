using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace SSCapture.Services;

internal sealed record ControlIdentity(string AutomationId, int Type);
internal sealed record ControlLookup(long Window, uint ProcessId, PixelBounds WindowBounds, PixelPoint? Point, ControlIdentity? Identity = null);
internal sealed record ControlHit(PixelBounds Bounds, ControlIdentity? Identity);
internal sealed record ControlLayoutEntry(ControlHit Hit, PixelBounds HitArea, bool Interactive, int Depth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SnapshotId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TreePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool InDocument = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Popup = false);
internal sealed record ControlLayout(ControlLayoutEntry[] Entries, bool Incomplete = false);

internal static partial class CaptureContextWorker
{
    private static ControlLayout ReadControlLayout(IAutomation automation, ControlLookup lookup)
    {
        var entries = new List<ControlLayoutEntry>();
        bool incomplete = false;
        LookupControl(automation, lookup, entries, () => incomplete = true);
        return new(entries.OrderByDescending(entry => entry.Interactive).ThenByDescending(entry => entry.Depth).ToArray(), incomplete);
    }

    private static ControlHit? LookupControl(IAutomation automation, ControlLookup lookup, List<ControlLayoutEntry>? layout = null,
        Action? markIncomplete = null)
    {
        nint handle = (nint)lookup.Window;
        if (GetWindowThreadProcessId(handle, out uint pid) == 0 || pid != lookup.ProcessId)
        {
            markIncomplete?.Invoke();
            return null;
        }
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
                if (browser) return Search(root, automation.RawViewWalker(), layout is null ? 1200 : 5000, 48);
                if (Search(root, automation.ControlViewWalker(), layout is null ? 220 : 1600, 24) is { } hit && layout is null) return hit;
            }

            ControlHit? Search(IElement start, ITreeWalker walker, int remaining, int depthLimit)
            {
                int matches = 0, bestDepth = -1;
                ControlHit? best = null;
                bool bestInteractive = false;
                try
                {
                    VisitChildren(start, 0, window);
                    if (remaining <= 0) markIncomplete?.Invoke();
                    return lookup.Identity is not null && (remaining <= 0 || matches != 1) ? null : best;
                }
                finally { Marshal.ReleaseComObject(walker); }

                void VisitChildren(IElement parent, int depth, Rectangle ancestorArea)
                {
                    if (remaining <= 0 || matches >= 2) return;
                    if (depth > depthLimit)
                    {
                        if (lookup.Identity is not null) remaining = 0;
                        if (layout is not null && walker.FirstChild(parent) is { } nested)
                        {
                            Marshal.ReleaseComObject(nested);
                            remaining = 0;
                        }
                        return;
                    }
                    IElement? child = walker.FirstChild(parent);
                    try
                    {
                        while (child is not null && remaining-- > 0)
                        {
                            bool visible = child.Property(30022) is not true;
                            Rectangle bounds = visible ? ReadControlBounds(child.Property(30001), window) : Rectangle.Empty;
                            if (visible && (pointer is null || bounds.IsEmpty || bounds.Contains(pointer.Value)))
                            {
                                Rectangle hitArea = bounds.IsEmpty ? ancestorArea : Rectangle.Intersect(ancestorArea, bounds);
                                int type = child.Property(30003) is int controlType ? controlType : 0;
                                bool interactive = IsInteractiveControl(type);
                                bool container = type == 50032 /* Window */ ||
                                    browser && type is 0 or 50025 /* Custom */ or 50026 /* Group */ or 50030 /* Document */ or 50033 /* Pane */;
                                if (!bounds.IsEmpty && (interactive || bounds != window) && !container)
                                {
                                    string? id = child.Property(30011) as string;
                                    ControlIdentity? identity = string.IsNullOrEmpty(id) || id.Length > 160 ? null : new(id, type);
                                    if (layout is not null && hitArea.Width > 0 && hitArea.Height > 0)
                                        layout.Add(new(new(PixelBounds.From(bounds), identity), PixelBounds.From(hitArea), interactive, depth));
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
                                if (layout is null || hitArea.Width > 0 && hitArea.Height > 0)
                                    VisitChildren(child, depth + 1, hitArea);
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
        catch (COMException) { markIncomplete?.Invoke(); }
        finally
        {
            if (root is not null) Marshal.ReleaseComObject(root);
        }
        if (layout is not null && !browser)
        {
            int remaining = 220;
            AddNativeChildren(handle, window, window, layout, depth: -24, ref remaining);
            if (remaining <= 0) markIncomplete?.Invoke();
        }
        return !browser && pointer is Point location && lookup.Identity is null ? NativeChild(handle, window, location) : null;
    }

    private static Rectangle ReadControlBounds(object? property, Rectangle window)
    {
        if (property is not double[] { Length: 4 } values
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

    private static void AddNativeChildren(nint parent, Rectangle window, Rectangle viewport, List<ControlLayoutEntry> layout, int depth, ref int remaining)
    {
        if (depth >= 0) return;
        for (nint child = GetWindow(parent, 5 /* GW_CHILD */); child != 0 && remaining-- > 0; child = GetWindow(child, 2 /* GW_HWNDNEXT */))
        {
            if (!IsWindowVisible(child) || !IsWindowEnabled(child) || (GetWindowLong(child, -20 /* GWL_EXSTYLE */) & 0x20) != 0
                || !GetWindowRect(child, out NativeRect native)) continue;
            var bounds = Rectangle.Intersect(viewport, Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            if (bounds != window) layout.Add(new(new(PixelBounds.From(bounds), null), PixelBounds.From(bounds), false, depth));
            AddNativeChildren(child, window, bounds, layout, depth + 1, ref remaining);
        }
    }

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
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint window, NativePoint point, uint flags);
}
