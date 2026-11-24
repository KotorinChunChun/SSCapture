namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private bool FrozenScreenshot => IsScreenshot && OutputSettings.FreezeFrame;
    private string? frozenSelectionError;
    private Rectangle? frozenControlPreviewArea;

    private Rectangle FrozenControlPreviewArea(CaptureWindow target, Rectangle control, FrozenCaptureTargets? targets)
    {
        Rectangle window = targets?.FullBounds(target) ?? Rectangle.Empty;
        return targets?.CanRead(target, window, Snapshot.Monitors) == true ? window : control;
    }

    private static WindowCaptureFrame ReadFrozenPreview(DesktopSnapshot snapshot, CaptureWindow target, IReadOnlyList<CaptureWindow> group,
        bool clientOnly, Rectangle? controlArea)
    {
        var targets = snapshot.FrozenTargets ?? throw new InvalidOperationException(FrozenCaptureTargets.WindowUnavailable);
        Rectangle? area = clientOnly ? target.ClientBoundsAtSelection ?? Rectangle.Empty : controlArea;
        return targets.ReadWindows(snapshot, group, area);
    }

    private CaptureSelection? PrepareFrozenTarget(CaptureSelection selection, CaptureWindow? target,
        IReadOnlyList<CaptureWindow> group, CaptureReplaySource.ControlGroup? controlGroup,
        bool isolated, bool clientOnly)
    {
        var frozen = Snapshot.FrozenTargets;
        if (controlGroup is not null)
        {
            if (frozen is null || controlGroup.Targets.Any(hit => !frozen.CanRead(hit.Window, hit.Bounds, SelectionMonitors)))
                return null;
            return selection;
        }
        if (selection.ControlSource is { } control)
            return frozen?.CanRead(control.Window, selection.Bounds, isolated ? Snapshot.Monitors : SelectionMonitors) == true ? selection : null;
        if (!isolated && !clientOnly) return selection;
        if (target is null) return null;

        Rectangle? client = clientOnly ? target.ClientBoundsAtSelection ?? Rectangle.Empty : null;
        if (!isolated)
            return client is Rectangle bounds && CaptureGeometry.IsFullyCovered(bounds, SelectionMonitors)
                ? selection with { Bounds = bounds } : null;
        Rectangle[]? regions = frozen?.WindowRegions(group, Snapshot.Monitors, client);
        if (regions is null) return null;
        if (selection.TargetMargin.HasValue)
            return regions.Length == 1 && regions[0].Contains(selection.Bounds) ? selection : null;
        return selection with
        {
            Bounds = regions.Aggregate(Rectangle.Union),
            Composite = regions.Length == 1 ? selection.Composite : new(Regions: regions, WindowRoots: selection.Composite?.WindowRoots)
        };
    }
}
