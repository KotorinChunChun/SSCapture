namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private sealed record MarginTarget(nint Window, Rectangle Original, Rectangle Source, int MinimumSize,
        bool Isolated, bool Control, Services.ControlIdentity? Identity = null, bool LiveScreenWindow = false);

    private MarginTarget? marginTarget;
    private int targetMargin, requestedTargetMargin, wheelRemainder;
    private bool targetWheelAdjusted;

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

    private bool PressedRectangleWindow => Mode == CaptureMode.Rectangle && !TwoPointSelection
        && pressedButton == MouseButtons.Left && gesture.IsPressed && !gesture.HasDragged;

    internal Point MagnifierSample => Pointer;

    internal Rectangle MagnifierSelection => SelectingDesktop ? gesture.MonitorAt(Pointer)
        : UsesWindowSource && !SelectingControl && PreviewFrame is null ? Rectangle.Empty : DisplaySelection;
    internal IReadOnlyList<Point>? MagnifierPolygon => !SelectingControl && Mode == CaptureMode.Freeform
        && gesture.HasDragged ? Points : null;

    internal IReadOnlyList<Rectangle> MagnifierRegions
    {
        get
        {
            if (HasControlDrag) return controlDrag!.Selected.Select(hit => hit.Bounds).Distinct().ToArray();
            if (FreeDesktopDrag && targetDrag is { Monitors.Count: > 0 }) return targetDrag.Monitors.ToArray();
            if (!HasWindowDrag) return [];
            if (!UsesWindowSource)
                return targetDrag!.BrightWindows.Select(bounds => CaptureGeometry.Clip(bounds, selectionBounds ?? Snapshot.Bounds))
                    .Where(CaptureGeometry.IsUsable).ToArray();
            return FrozenScreenshot
                ? Snapshot.FrozenTargets?.WindowRegions(targetDrag!.IncludedWindows.ToArray(), Snapshot.Monitors) ?? [] : [];
        }
    }

    private MarginTarget? VisibleMarginTarget => marginTarget is { } target
        && (target.Control || HighlightWindow || HasPointerPress) ? target : null;

    private Rectangle? MarginSelection => VisibleMarginTarget is { } target
        ? Rectangle.Inflate(target.Original, targetMargin, targetMargin) : null;

    internal Rectangle SelectionBasis => VisibleMarginTarget?.Original
        ?? (SelectingControl || PreviewFrame is not null ? Rectangle.Empty : gesture.SelectionBasis);

    internal string? SelectionMarginHint
    {
        get
        {
            if (VisibleMarginTarget is not null ? !targetWheelAdjusted : !gesture.HasWheelAdjustment) return null;
            Rectangle basis = SelectionBasis, frame = DisplaySelection;
            if (basis.Width <= 0 || basis.Height <= 0 || frame.Width <= 0 || frame.Height <= 0) return null;
            int left = basis.Left - frame.Left, top = basis.Top - frame.Top;
            int right = frame.Right - basis.Right, bottom = frame.Bottom - basis.Bottom;
            if (left == right && top == bottom)
                return left == top ? left == 0 ? "0 px" : $"各辺 {Format(left)}"
                    : $"左右 {Format(left)} ／ 上下 {Format(top)}";
            return $"左 {Format(left)} ／ 上 {Format(top)} ／ 右 {Format(right)} ／ 下 {Format(bottom)}";

            static string Format(int value) => $"{value:+0;-0;0} px";
        }
    }

    private int? AppliedTargetMargin => targetMargin == 0 ? null : targetMargin;

    private void ResetTargetMargin()
    {
        marginTarget = null;
        wheelRemainder = requestedTargetMargin = targetMargin = 0;
        targetWheelAdjusted = false;
    }

    private bool UpdateTargetMargin()
    {
        MarginTarget? next = ReadMarginTarget();
        if (next == marginTarget) return false;
        // Keep the adjustment while the live window moves. Re-clamp it to the
        // new physical bounds without treating the move as a different target.
        if (next is not { LiveScreenWindow: true } || marginTarget is not { LiveScreenWindow: true } previous
            || previous.Window != next.Window)
            ResetTargetMargin();
        marginTarget = next;
        ReapplyTargetMargin();
        return true;
    }

    private void RefreshLiveWindowMargin()
    {
        if (marginTarget is not { LiveScreenWindow: true } || !UpdateTargetMargin()) return;
        UpdateGuidePointer();
        overlayUpdatePending = true;
    }

    private void ReapplyTargetMargin()
    {
        targetMargin = marginTarget is { Original.IsEmpty: false } target
            ? CaptureTargetMargin.Apply(target.Original, requestedTargetMargin, target.Source,
                target.MinimumSize, target.Isolated ? null : SelectionMonitors).Margin
            : 0;
    }

    private MarginTarget? ReadMarginTarget()
    {
        if (completed || refreshing || menuOpen || menuTransition || HasControlDrag || !IsScreenshot) return null;
        if (SelectingControl)
        {
            if (controlCandidate is not { } control) return null;
            bool isolated = UsesControlPreview || UsesWindowSource;
            if (isolated && !ControlPreviewReady) return null;
            return new(control.Window.Handle, control.Bounds, isolated ? PreviewFrame!.Bounds : Snapshot.Bounds,
                1, isolated, true, control.Identity);
        }

        if (SelectingDesktop || HasWindowDrag || TwoPointSelection || UsesClientWindowArea
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
        if (window is not null && !OutputSettings.FreezeFrame && (PressedRectangleWindow || HighlightWindow))
        {
            Rectangle liveBounds;
            try { liveBounds = captureWindowsKey ? CaptureNative.RequireCaptureWindow(window) : window.Bounds; }
            catch (InvalidOperationException) { liveBounds = Rectangle.Empty; }
            original = CaptureGeometry.Clip(liveBounds, source);
            if (!CaptureGeometry.IsUsable(original)) original = Rectangle.Empty;
            return new(window.Handle, original, source, CaptureGeometry.MinimumSize, false, false, LiveScreenWindow: true);
        }
        return new(window?.Handle ?? 0, original, source, CaptureGeometry.MinimumSize, false, false);
    }

    private bool FixedWheelEnabled => !options.RequireExactFixedSize && OutputSettings.CaptureKeys.WheelAdjustRectangle;

    internal void MouseWheel(Point pointer, int delta)
    {
        if (completed || refreshing || menuOpen || menuTransition || confirmingControl || delta == 0) return;
        Move(true, pointer);
        if (!SelectingControl && !SelectingDesktop && (Mode == CaptureMode.FixedRegion
            ? FixedWheelEnabled && gesture.ResizeFixedFrame(delta)
            : OutputSettings.CaptureKeys.WheelAdjustRectangle && (HasPointerPress || waitingForSecondPoint) && gesture.ResizeRectangle(delta)))
        {
            hasSelectionInput = true;
            RefreshOverlays();
            return;
        }
        if (!SelectingControl && !HighlightWindow
            && (!PressedRectangleWindow || !OutputSettings.CaptureKeys.WheelAdjustRectangle)) return;
        UpdateTargetMargin();
        if (marginTarget is not { Original.IsEmpty: false }) return;
        (int steps, wheelRemainder) = CaptureTargetMargin.WheelSteps(wheelRemainder, delta);
        hasSelectionInput = true;
        if (steps != 0)
        {
            targetWheelAdjusted = true;
            requestedTargetMargin = targetMargin + steps;
            ReapplyTargetMargin();
        }
        RefreshOverlays();
    }

    private CaptureSelection? ApplyWindowMargin(CaptureSelection selection)
    {
        if (selection.WindowHitPoint is null || marginTarget is not { Control: false } target
            || !target.LiveScreenWindow && AppliedTargetMargin is null)
            return selection;
        if (target.Original.IsEmpty) return null;
        return selection with { Bounds = Rectangle.Inflate(target.Original, targetMargin, targetMargin), TargetMargin = AppliedTargetMargin };
    }
}
