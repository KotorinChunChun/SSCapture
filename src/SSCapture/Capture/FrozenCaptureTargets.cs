using System.Collections.Concurrent;
using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record FrozenControl(nint Handle, Rectangle Bounds);

internal sealed record FrozenWindow(CaptureWindow Target, Rectangle FullBounds, bool Selectable, bool Layered,
    IReadOnlyList<FrozenControl>? Controls = null)
{
    // Z order is checked against the foreground windows in each observation.
    internal bool SameGeometry(FrozenWindow other) => (Target with { Title = "", ZOrder = 0 }) == (other.Target with { Title = "", ZOrder = 0 })
        && FullBounds == other.FullBounds && Selectable == other.Selectable && Layered == other.Layered;
}

internal sealed class FrozenCaptureTargets
{
    internal const string WindowUnavailable = "開始時のウィンドウ全体を確認できません。別の対象か、範囲を選んでください。";
    internal const string ControlUnavailable = "開始時のコントロールを確認できません。Ctrlを離すと範囲を選べます。";

    private readonly Dictionary<nint, WindowObservation> observations = [];
    private readonly ConcurrentDictionary<nint, ControlLayout> layouts = new();

    internal IReadOnlyList<CaptureWindow> SelectableWindows { get; }

    private sealed record WindowObservation(FrozenWindow Before, FrozenWindow After, FrozenWindow[] BeforeCover, FrozenWindow[] AfterCover);

    internal FrozenCaptureTargets(IReadOnlyList<FrozenWindow> before, IReadOnlyList<FrozenWindow>? after = null)
    {
        after ??= before;
        SelectableWindows = before.Where(window => window.Selectable).Select(window => window.Target).ToArray();
        var byHandle = after.ToDictionary(window => window.Target.Handle);
        foreach (var window in before)
        {
            if (!window.Selectable || window.Layered || !byHandle.TryGetValue(window.Target.Handle, out var current)
                || !window.SameGeometry(current)) continue;
            observations.Add(window.Target.Handle, new(window, current, CoveringWindows(before, window), CoveringWindows(after, current)));
        }
    }

    internal static bool IsBrowser(CaptureWindow window) =>
        window.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || window.ClassName == "MozillaWindowClass";

    internal static FrozenCaptureTargets Compare(IReadOnlyList<FrozenWindow> before, IReadOnlyList<FrozenWindow> after) => new(before, after);

    internal static IEnumerable<FrozenWindow> VisibleBrowsers(IReadOnlyList<FrozenWindow> windows, IReadOnlyList<Rectangle> monitors) =>
        windows.Where(window => window.Selectable && !window.Layered && IsBrowser(window.Target)
            && monitors.Any(monitor => PartlyVisible(window, monitor, windows)));

    private static bool PartlyVisible(FrozenWindow window, Rectangle monitor, IReadOnlyList<FrozenWindow> windows)
    {
        var visible = Rectangle.Intersect(window.FullBounds, monitor);
        return CaptureGeometry.IsUsable(visible) && !CaptureGeometry.IsFullyCovered(visible, CoveringWindows(windows, window).Select(cover => cover.FullBounds).ToArray());
    }

    private static FrozenWindow[] CoveringWindows(IReadOnlyList<FrozenWindow> windows, FrozenWindow target) => windows
        .Where(front => front.Target.ZOrder < target.Target.ZOrder && front.FullBounds.IntersectsWith(target.FullBounds))
        .ToArray();

    private WindowObservation? Observe(CaptureWindow window) =>
        observations.TryGetValue(window.Handle, out var observation) && observation.Before.Target == window ? observation : null;

    internal bool CanRead(CaptureWindow window, Rectangle area, IReadOnlyList<Rectangle> monitors) =>
        Observe(window) is { } observation && IsVisible(observation, area) && CaptureGeometry.IsFullyCovered(area, monitors);

    private static bool IsVisible(WindowObservation observation, Rectangle area) =>
        observation.Before.FullBounds.Contains(area) && !observation.BeforeCover.Any(cover => area.IntersectsWith(cover.FullBounds))
        && !observation.AfterCover.Any(cover => area.IntersectsWith(cover.FullBounds));

    internal Rectangle FullBounds(CaptureWindow window) => Observe(window)?.Before.FullBounds ?? Rectangle.Empty;

    internal void SetBrowserControls(CaptureWindow window, Rectangle sampledBounds, ControlLayout? before, ControlLayout? after)
    {
        if (!observations.TryGetValue(window.Handle, out var observation) || observation.Before.FullBounds != sampledBounds
            || !observation.Before.SameGeometry(new(window, sampledBounds, observation.Before.Selectable, observation.Before.Layered))
            || before is not { Incomplete: false, Entries.Length: > 0 } || after is not { Incomplete: false }) return;

        var stable = UnambiguousEntries(after.Entries).ToHashSet();
        var entries = UnambiguousEntries(before.Entries)
            .Where(entry => stable.Contains(entry) && IsVisible(observation, ToRectangle(entry.Hit.Bounds))).ToArray();
        layouts[window.Handle] = new(entries);
    }

    private static IEnumerable<ControlLayoutEntry> UnambiguousEntries(ControlLayoutEntry[] entries)
    {
        var rejected = entries.GroupBy(entry => entry.SnapshotId)
            .Where(group => group.Key is null || group.Count() != 1).SelectMany(group => group).ToHashSet();
        var ordered = entries.OrderBy(item => item.HitArea.X).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var first = ordered[i];
            Rectangle bounds = ToRectangle(first.HitArea);
            for (int j = i + 1; j < ordered.Length && ordered[j].HitArea.X < bounds.Right; j++)
            {
                var second = ordered[j];
                if (bounds.IntersectsWith(ToRectangle(second.HitArea)) && !IsAncestor(first, second) && !IsAncestor(second, first))
                {
                    // Chromium can report toolbar buttons past the edge of its chrome. A document
                    // control and a browser popup still conflict, as do controls within one tree.
                    if (first.InDocument != second.InDocument)
                    {
                        var chrome = first.InDocument ? second : first;
                        var document = first.InDocument ? first : second;
                        rejected.Add(chrome);
                        if (chrome.Popup || ToRectangle(chrome.HitArea).Contains(ToRectangle(document.HitArea)))
                            rejected.Add(document);
                    }
                    else
                    {
                        rejected.Add(first);
                        rejected.Add(second);
                    }
                }
            }
        }
        return entries.Where(entry => entry.TreePath is not null && !rejected.Contains(entry));

        static bool IsAncestor(ControlLayoutEntry ancestor, ControlLayoutEntry descendant) =>
            ancestor.TreePath is { } parent && descendant.TreePath is { } child && child.Length > parent.Length
            && child[parent.Length] == '/' && child.StartsWith(parent, StringComparison.Ordinal);
    }

    private ControlLayout? ReadLayout(CaptureWindow window)
    {
        if (Observe(window) is not { } observation) return null;
        if (layouts.TryGetValue(window.Handle, out var cached)) return cached;
        if (IsBrowser(window) || observation.Before.Controls is not { } before || observation.After.Controls is not { } after)
            return null;
        var confirmed = after.ToHashSet();
        cached = new(before.Where(control => confirmed.Contains(control) && IsVisible(observation, control.Bounds))
            .Select(control =>
            {
                var bounds = PixelBounds.From(control.Bounds);
                return new ControlLayoutEntry(new(bounds, null), bounds, false, 0);
            }).ToArray());
        layouts.TryAdd(window.Handle, cached);
        return cached;
    }

    internal ControlLayout? ControlLayout(CaptureWindow window, IReadOnlyList<Rectangle> monitors) =>
        ReadLayout(window) is { } layout ? new(layout.Entries.Where(entry => CaptureGeometry.IsFullyCovered(ToRectangle(entry.Hit.Bounds), monitors)).ToArray()) : null;

    internal ControlLayoutEntry? HitControl(CaptureWindow window, Point point, IReadOnlyList<Rectangle> monitors) =>
        ReadLayout(window)?.Entries.FirstOrDefault(entry => ToRectangle(entry.HitArea).Contains(point)
            && CaptureGeometry.IsFullyCovered(ToRectangle(entry.Hit.Bounds), monitors));

    private static Rectangle ToRectangle(PixelBounds bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    internal WindowCaptureFrame ReadWindows(DesktopSnapshot snapshot, IReadOnlyList<CaptureWindow> targets, Rectangle? client = null)
    {
        Rectangle[] regions = WindowRegions(targets, snapshot.Monitors, client) ?? throw new InvalidOperationException(WindowUnavailable);
        Rectangle bounds = regions.Aggregate(Rectangle.Union);
        return new(CaptureGeometry.ComposeRegions(snapshot.Image, snapshot.Bounds, bounds, regions), bounds, snapshot.CapturedAt);
    }

    internal Rectangle[]? WindowRegions(IReadOnlyList<CaptureWindow> targets, IReadOnlyList<Rectangle> monitors, Rectangle? client = null)
    {
        if (targets.Count == 0) return null;
        var included = targets.Select(window => window.Handle).ToHashSet();
        Rectangle[] regions = targets.Select(window => client ?? FullBounds(window)).ToArray();
        for (int i = 0; i < targets.Count; i++)
        {
            if (Observe(targets[i]) is not { } window || !CaptureGeometry.IsFullyCovered(regions[i], monitors)) return null;
            if (IsVisible(window, regions[i])) continue;
            // A group may cover itself. Every visible rectangle must still come from a selected member.
            if (!window.Before.FullBounds.Contains(regions[i]) ||
                window.BeforeCover.Any(cover => cover.FullBounds.IntersectsWith(regions[i]) && !included.Contains(cover.Target.Handle)) ||
                window.AfterCover.Any(cover => cover.FullBounds.IntersectsWith(regions[i]) && !included.Contains(cover.Target.Handle))) return null;
        }
        return regions;
    }
}
