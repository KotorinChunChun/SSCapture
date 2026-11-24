using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record FrozenControl(nint Handle, Rectangle Bounds);

internal sealed record FrozenWindow(CaptureWindow Target, Rectangle FullBounds, bool Selectable, bool Layered,
    IReadOnlyList<FrozenControl>? Controls = null)
{
    // Titles may change without moving any pixels between windows.
    internal bool SameGeometry(FrozenWindow other) => (Target with { Title = "" }) == (other.Target with { Title = "" })
        && FullBounds == other.FullBounds && Selectable == other.Selectable && Layered == other.Layered;
}

internal sealed class FrozenCaptureTargets(IReadOnlyList<FrozenWindow> windows, bool stable = true)
{
    internal const string WindowUnavailable = "固定した画面に対象の全体がありません。別の対象を選ぶか、画面を取り直してください。";
    internal const string ControlUnavailable = "開始時のコントロールの位置を確認できません。範囲で選ぶか、画面固定をOFFにしてください。";

    private readonly Dictionary<nint, FrozenWindow> byHandle = windows.ToDictionary(entry => entry.Target.Handle);
    private (nint Window, ControlLayout Layout)? browserControls;
    internal IReadOnlyList<CaptureWindow> SelectableWindows { get; } = windows.Where(entry => entry.Selectable)
        .Select(entry => entry.Target).ToArray();
    internal static bool IsBrowser(CaptureWindow window) =>
        window.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || window.ClassName == "MozillaWindowClass";

    internal static FrozenCaptureTargets Compare(IReadOnlyList<FrozenWindow> before, IReadOnlyList<FrozenWindow> after)
    {
        bool stable = before.Count == after.Count && before.Zip(after).All(pair => pair.First.SameGeometry(pair.Second));
        if (!stable) return new(before, stable: false);

        return new(before.Zip(after, (first, second) => first with
        {
            Controls = first.Controls is { } controls && second.Controls is { } latest && controls.SequenceEqual(latest)
                ? controls : null
        }).ToArray());
    }

    internal bool CanRead(CaptureWindow window, Rectangle area, IReadOnlyList<Rectangle> monitors,
        IReadOnlySet<nint>? included = null) =>
        stable && byHandle.TryGetValue(window.Handle, out var entry) && entry.Selectable && !entry.Layered
        && entry.Target == window && entry.FullBounds.Contains(area) && CaptureGeometry.IsFullyCovered(area, monitors)
        && !windows.Any(front => front.Target.ZOrder < window.ZOrder && front.FullBounds.IntersectsWith(area)
            && included?.Contains(front.Target.Handle) != true);

    internal Rectangle FullBounds(CaptureWindow window) => byHandle.TryGetValue(window.Handle, out var entry)
        ? entry.FullBounds : Rectangle.Empty;

    internal void SetBrowserControls(CaptureWindow window, Rectangle sampledBounds, ControlLayout? before, ControlLayout? after)
    {
        if (!stable || !byHandle.TryGetValue(window.Handle, out var entry) || entry.FullBounds != sampledBounds
            || entry.Target.ProcessId != window.ProcessId || entry.Target.ThreadId != window.ThreadId
            || before is not { Incomplete: false, Entries.Length: > 0 } || after is not { Incomplete: false }
            || !before.Entries.SequenceEqual(after.Entries)
            || before.Entries.Any(item => item.SnapshotId is null || item.TreePath is null)
            || before.Entries.Select(item => item.SnapshotId).Distinct().Count() != before.Entries.Length)
            return;

        var ambiguous = new HashSet<ControlLayoutEntry>();
        var ordered = before.Entries.OrderBy(item => item.HitArea.X).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var first = ordered[i];
            Rectangle bounds = ToRectangle(first.HitArea);
            for (int j = i + 1; j < ordered.Length && ordered[j].HitArea.X < bounds.Right; j++)
            {
                var second = ordered[j];
                if (bounds.IntersectsWith(ToRectangle(second.HitArea)) &&
                    !IsAncestor(first, second) && !IsAncestor(second, first))
                {
                    ambiguous.Add(first);
                    ambiguous.Add(second);
                }
            }
        }
        var uncertain = ambiguous.Select(item => ToRectangle(item.HitArea)).ToArray();
        browserControls = (window.Handle, new(before.Entries.Where(item =>
            !uncertain.Any(area => area.IntersectsWith(ToRectangle(item.HitArea)))).ToArray()));

        static bool IsAncestor(ControlLayoutEntry ancestor, ControlLayoutEntry descendant) =>
            descendant.TreePath!.Length > ancestor.TreePath!.Length
            && descendant.TreePath[ancestor.TreePath.Length] == '/'
            && descendant.TreePath.StartsWith(ancestor.TreePath, StringComparison.Ordinal);
    }

    internal ControlLayout? ControlLayout(CaptureWindow window, IReadOnlyList<Rectangle> monitors)
    {
        if (!stable || !byHandle.TryGetValue(window.Handle, out var entry) || entry.Target != window)
            return null;
        if (browserControls is { } browser && browser.Window == window.Handle)
            return new(browser.Layout.Entries.Where(item => CanRead(window, ToRectangle(item.Hit.Bounds), monitors)).ToArray());
        if (IsBrowser(window) || entry.Controls is not { } controls) return null;
        return new(controls.Where(control => CanRead(window, control.Bounds, monitors)).Select(control =>
        {
            var bounds = PixelBounds.From(control.Bounds);
            return new ControlLayoutEntry(new(bounds, null), bounds, false, 0);
        }).ToArray());
    }

    private static Rectangle ToRectangle(PixelBounds bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    internal WindowCaptureFrame ReadWindows(DesktopSnapshot snapshot, IReadOnlyList<CaptureWindow> targets, Rectangle? client = null)
    {
        Rectangle[] regions = WindowRegions(targets, snapshot.Monitors, client) ?? throw new InvalidOperationException(WindowUnavailable);
        Rectangle bounds = regions.Aggregate(Rectangle.Union);
        return new(CaptureGeometry.ComposeRegions(snapshot.Image, snapshot.Bounds, bounds, regions), bounds, snapshot.CapturedAt);
    }

    internal Rectangle[]? WindowRegions(IReadOnlyList<CaptureWindow> targets, IReadOnlyList<Rectangle> monitors, Rectangle? client = null)
    {
        var included = targets.Select(window => window.Handle).ToHashSet();
        Rectangle[] regions = targets.Select(window => client ?? FullBounds(window)).ToArray();
        for (int i = 0; i < targets.Count; i++)
            if (!CanRead(targets[i], regions[i], monitors, included)) return null;
        return regions.Length == 0 ? null : regions;
    }
}
