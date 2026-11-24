using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal static partial class CaptureContextWorker
{
    // Read the subtree and its properties in one provider call. The walk below only reads the cached values.
    private static ControlLayout ReadFrozenControlLayout(IAutomation automation, ControlLookup lookup)
    {
        nint handle = (nint)lookup.Window;
        if (GetWindowThreadProcessId(handle, out uint pid) == 0 || pid != lookup.ProcessId) return new([], Incomplete: true);
        var bounds = lookup.WindowBounds;
        var window = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        IAutomationCacheRequest? request = null;
        IElement? root = null;
        nint filter = 0;
        int remaining = 5000;
        bool complete = true;
        var entries = new List<ControlLayoutEntry>();
        try
        {
            request = automation.CreateCacheRequest();
            filter = automation.RawViewCondition();
            request.SetTreeFilter(filter);
            request.SetTreeScope(7); // TreeScope_Subtree
            request.SetElementMode(0); // AutomationElementMode_None
            foreach (int property in new[] { 30000, 30001, 30003, 30011, 30022, 30174 }) request.AddProperty(property);
            root = automation.FromHandleWithCache(handle, request);
            if (root is null) return new([], Incomplete: true);
            Visit(root, 0, window, "", false, false);
        }
        catch (COMException) { return new([], Incomplete: true); }
        finally
        {
            if (root is not null) Marshal.ReleaseComObject(root);
            if (filter != 0) Marshal.Release(filter);
            if (request is not null) Marshal.ReleaseComObject(request);
        }
        return new(entries.OrderByDescending(entry => entry.Interactive).ThenByDescending(entry => entry.Depth).ToArray(), !complete);

        void Visit(IElement parent, int depth, Rectangle ancestor, string path, bool inDocument, bool popup)
        {
            if (!complete || parent.CachedChildren() is not { } children) return;
            try
            {
                int count = children.Length();
                if (depth > 48 || count > remaining) { complete = false; return; }
                for (int index = 0; index < count && complete; index++)
                {
                    var child = children.Element(index);
                    if (child is null || remaining-- == 0) { complete = false; return; }
                    try
                    {
                        if (child.CachedProperty(30022) is true) continue;
                        Rectangle area = ReadControlBounds(child.CachedProperty(30001), window);
                        Rectangle hit = area.IsEmpty ? ancestor : Rectangle.Intersect(area, ancestor);
                        if (hit.Width <= 0 || hit.Height <= 0) continue;
                        int type = child.CachedProperty(30003) is int number ? number : 0;
                        bool documentChild = inDocument || type == 50030 /* Document */;
                        bool popupChild = popup || type is 50009 /* Menu */ or 50011 /* MenuItem */ or 50022 /* ToolTip */
                            || child.CachedProperty(30174) is true; // IsDialog
                        string childPath = $"{path}/{index}";
                        bool interactive = IsInteractiveControl(type);
                        bool container = type is 0 or 50025 or 50026 or 50030 or 50032 or 50033;
                        if (!container && !area.IsEmpty && (interactive || area != window))
                        {
                            string? snapshotId = child.CachedProperty(30000) is int[] { Length: > 0 } runtimeId
                                ? string.Join(',', runtimeId) : null;
                            string? id = child.CachedProperty(30011) as string;
                            ControlIdentity? identity = string.IsNullOrEmpty(id) || id.Length > 160 ? null : new(id, type);
                            entries.Add(new(new(PixelBounds.From(area), identity), PixelBounds.From(hit), interactive, depth,
                                snapshotId, childPath, documentChild, popupChild));
                        }
                        Visit(child, depth + 1, hit, childPath, documentChild, popupChild);
                    }
                    finally { Marshal.ReleaseComObject(child); }
                }
            }
            finally { Marshal.ReleaseComObject(children); }
        }
    }

    // Windows SDK UIAutomationClient.h vtable order.
    [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomationCacheRequest
    {
        void AddProperty(int id);
        void AddPattern(int id);
        nint Clone();
        int TreeScope();
        void SetTreeScope(int scope);
        nint TreeFilter();
        void SetTreeFilter(nint filter);
        int ElementMode();
        void SetElementMode(int mode);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomationElementArray
    {
        int Length();
        IElement? Element(int index);
    }
}
