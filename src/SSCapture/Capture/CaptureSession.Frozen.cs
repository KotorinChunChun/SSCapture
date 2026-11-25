namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private bool FrozenScreenshot => IsScreenshot && OutputSettings.FreezeFrame;
    private string? frozenSelectionError;
    private Rectangle? frozenControlPreviewArea;

    private Rectangle CachedClickBounds(CaptureSingleClickAction action, Point point) => action switch
    {
        CaptureSingleClickAction.Window => gesture.WindowAt(point),
        CaptureSingleClickAction.Desktop => gesture.MonitorAt(point),
        CaptureSingleClickAction.Control => CachedControlAt(point)?.Bounds ?? Rectangle.Empty,
        _ => Rectangle.Empty
    };

    private CaptureControlTarget? CachedControlAt(Point point)
    {
        var window = findWindow?.Invoke(point);
        var entry = window is null ? null : Snapshot.FrozenTargets?.HitControl(window, point, SelectionMonitors);
        return entry is null ? null : new(window!, ToRectangle(entry.Hit.Bounds), entry.Hit.Identity);
    }

    internal async void ConfirmCachedClick(CaptureSingleClickAction action, Point point)
    {
        var snapshot = Snapshot;
        if (action == CaptureSingleClickAction.Control)
        {
            confirmingControl = true;
            try
            {
                RefreshOverlays();
                await snapshot.FrozenControlsReady;
                if (completed || refreshing || !ReferenceEquals(snapshot, Snapshot)) return;
                if (CachedControlAt(point) is { } control)
                {
                    var source = new CaptureReplaySource.Control(control.Window, control.Identity, false);
                    CompleteSelection(new(control.Bounds, null, Destination, ControlSource: source), useStartFrame: true);
                }
                else frozenSelectionError = FrozenCaptureTargets.ControlUnavailable;
            }
            finally
            {
                if (ReferenceEquals(snapshot, Snapshot)) confirmingControl = false;
                if (!completed) RefreshOverlays();
            }
            return;
        }

        CaptureSelection? selection = action switch
        {
            CaptureSingleClickAction.Window when gesture.WindowSelection(point, Destination) is { } window => ApplyWindowMargin(window),
            CaptureSingleClickAction.Desktop => gesture.MonitorSelection(point, Destination),
            _ => null
        };
        if (selection is not null) CompleteSelection(selection, entireMonitor: action == CaptureSingleClickAction.Desktop, useStartFrame: true);
        else RefreshOverlays();
    }

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
