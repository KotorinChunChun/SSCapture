namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private sealed record MarginTarget(nint Window, Rectangle Original, Rectangle Source, int MinimumSize,
        bool Isolated, bool Control, Services.ControlIdentity? Identity = null);

    private MarginTarget? marginTarget;
    private int targetMargin, wheelRemainder;
    private bool targetWheelUsed;

    private void WatchGuideBounds(UI.SelectionGuideForm form)
    {
        form.LocationChanged += GuideBoundsChanged;
        form.SizeChanged += GuideBoundsChanged;
        form.VisibleChanged += GuideBoundsChanged;
        GuideBoundsChanged(form, EventArgs.Empty);

        void GuideBoundsChanged(object? sender, EventArgs args)
        {
            if (completed || !ReferenceEquals(guide, form) || !OutputSettings.ShowMagnifier) return;
            overlayUpdatePending = true;
            if (snapTimer?.Enabled != true) UpdateOverlays();
        }
    }

    internal Point MagnifierSample => !SelectingControl && pressedButton != MouseButtons.Middle
        && (Mode == CaptureMode.Rectangle || Mode == CaptureMode.FixedRegion && gesture.FixedPosition is null)
        && MarginSelection is null ? gesture.MagnifierPoint : Pointer;

    internal Rectangle MagnifierSelection => pressedButton == MouseButtons.Middle ? gesture.MonitorAt(Pointer)
        : UsesWindowSource && !SelectingControl && PreviewFrame is null ? Rectangle.Empty : DisplaySelection;
    internal IReadOnlyList<Point>? MagnifierPolygon => !SelectingControl && Mode == CaptureMode.Freeform
        && gesture.HasDragged ? Points : null;

    private Rectangle? MarginSelection => marginTarget is { } target
        && (target.Control || HighlightWindow || HasPointerPress || targetWheelUsed)
        ? Rectangle.Inflate(target.Original, targetMargin, targetMargin) : null;

    private int? AppliedTargetMargin => targetMargin == 0 ? null : targetMargin;

    private void ResetTargetMargin()
    {
        marginTarget = null;
        wheelRemainder = targetMargin = 0;
        targetWheelUsed = false;
    }

    private void UpdateTargetMargin()
    {
        MarginTarget? next = ReadMarginTarget();
        if (next == marginTarget) return;
        ResetTargetMargin();
        marginTarget = next;
    }

    private MarginTarget? ReadMarginTarget()
    {
        if (completed || refreshing || menuOpen || menuTransition || options.Intent != CaptureIntent.Screenshot) return null;
        if (SelectingControl)
        {
            if (controlCandidate is not { } control) return null;
            bool isolated = UsesControlPreview || UsesWindowSource;
            if (isolated && !ControlPreviewReady) return null;
            return new(control.Window.Handle, control.Bounds, isolated ? PreviewFrame!.Bounds : Snapshot.Bounds,
                1, isolated, true, control.Identity);
        }

        if (pressedButton == MouseButtons.Middle || HasWindowDrag || TwoPointSelection
            || Mode is not (CaptureMode.Window or CaptureMode.Rectangle or CaptureMode.Freeform)
            || gesture.IsPressed && gesture.HasDragged) return null;
        Point point = gesture.WindowTargetPoint;
        Rectangle original = gesture.WindowAt(point);
        if (!CaptureGeometry.IsUsable(original)) return null;
        CaptureWindow? window = findWindow?.Invoke(point);
        if (UsesWindowSource)
        {
            if (window is null || windowPreview is not { Windows.Count: 1 } preview || preview.Target != window
                || PreviewFrame is not { } frame || !CaptureGeometry.IsUsable(frame.Bounds)) return null;
            return new(window.Handle, frame.Bounds, frame.Bounds, CaptureGeometry.MinimumSize, true, false);
        }

        Rectangle source = selectionBounds ?? Snapshot.Bounds;
        return new(window?.Handle ?? 0, original, source, CaptureGeometry.MinimumSize, false, false);
    }

    internal void MouseWheel(Point pointer, int delta)
    {
        if (completed || refreshing || menuOpen || menuTransition || confirmingControl || delta == 0) return;
        Move(true, pointer);
        UpdateTargetMargin();
        if (marginTarget is not { } target) return;
        (int steps, wheelRemainder) = CaptureTargetMargin.WheelSteps(wheelRemainder, delta);
        hasSelectionInput = true;
        targetWheelUsed = true;
        var adjusted = CaptureTargetMargin.Apply(target.Original, targetMargin + steps, target.Source,
            target.MinimumSize, target.Isolated ? null : SelectionMonitors);
        targetMargin = adjusted.Margin;
        RefreshOverlays();
    }

    private CaptureSelection ApplyWindowMargin(CaptureSelection selection)
    {
        if (selection.WindowHitPoint is null || marginTarget is not { Control: false } target || AppliedTargetMargin is not int margin)
            return selection;
        return selection with { Bounds = Rectangle.Inflate(target.Original, margin, margin), TargetMargin = margin };
    }
}
