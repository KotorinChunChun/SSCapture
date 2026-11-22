using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private nint focusBeforeSelection;
    private bool selectingControl, controlPress, confirmingControl, controlHoldCancelled;
    private bool previewControl;
    private CaptureControlDrag? controlDrag;
    private Point controlDragStart;
    private Size controlDragThreshold;
    private CaptureControlTarget? startingControl;
    private Task? controlDragLookup;
    internal bool HasControlDrag => controlDrag is not null;
    internal int SelectedControlCount => controlDrag?.Selected.Count ?? 0;
    private Task? controlLookup;
    private string? controlConfirmationError;
    private int controlVersion, controlActivation;
    private (Point Point, CaptureWindow? Window)? controlRequest;
    private (CaptureWindow Window, Rectangle Bounds, ControlIdentity? Identity)? controlCandidate;
    internal Rectangle ControlBounds => MarginSelection ?? controlCandidate?.Bounds ?? Rectangle.Empty;
    internal bool SelectingControl => selectingControl;
    internal bool UsesControlPreview => selectingControl && previewControl && !HasControlDrag;
    private bool ControlPreviewReady => windowPreviewIsControl && controlCandidate is { } candidate
        && windowPreview?.Target == candidate.Window && PreviewFrame is { } frame && frame.Bounds.Contains(candidate.Bounds);

    private void UpdateControlSelection(bool enabled, Point pointer, bool preview)
    {
        if (!enabled)
        {
            if (suppressedButtons == MouseButtons.None) controlHoldCancelled = false;
            ExitControlSelection();
            return;
        }
        if (controlHoldCancelled) return;
        if (!selectingControl)
        {
            selectingControl = true;
            previewControl = preview;
            controlConfirmationError = null;
            if (focusBeforeSelection != 0 && CaptureNative.OwnsForegroundWindow()
                && CaptureNative.SetForegroundWindow(focusBeforeSelection))
            {
                foreach (CaptureOverlay overlay in overlays)
                    if (overlay.Visible) overlay.RaiseWithoutActivation();
                if (guide?.Visible == true) CaptureNative.RaiseWithoutActivation(guide.Handle);
                if (dragOwner is not null && !dragOwner.Capture) dragOwner.Capture = true;
            }
        }
        if (HasControlDrag) return;
        CaptureWindow? window = findWindow?.Invoke(pointer);
        if (UsesControlPreview && controlCandidate is { } selected && selected.Bounds.Contains(pointer)
            && window?.Handle != selected.Window.Handle)
            return; // The preview stays selectable over the window that originally covered it.
        if (controlRequest is { } previous && previous.Point == pointer && previous.Window == window) return;
        controlRequest = (pointer, window);
        controlVersion++;
        if (controlCandidate is { } candidate && (candidate.Window.Handle != window?.Handle || !candidate.Bounds.Contains(pointer)))
        {
            controlCandidate = null;
            RefreshOverlays();
        }
        if (controlLookup is not { IsCompleted: false } && window is not null) controlLookup = LookupControlsAsync();
    }

    private async Task LookupControlsAsync()
    {
        while (selectingControl && controlRequest is { Window: { } window, Point: var point })
        {
            int version = controlVersion;
            int activation = controlActivation;
            ControlHit? hit = null;
            CaptureWindow observedWindow = window;
            Rectangle bounds = window.Bounds;
            try
            {
                Rectangle actual = CaptureNative.RequireCaptureWindow(window);
                if (!OutputSettings.FreezeFrame || bounds == Rectangle.Intersect(actual, Snapshot.Bounds))
                {
                    observedWindow = window with { Bounds = actual };
                    hit = await CaptureContextClient.Shared.FindControlAsync(new(window.Handle.ToInt64(), window.ProcessId,
                        PixelBounds.From(actual), PixelPoint.From(point)));
                }
            }
            catch (InvalidOperationException) { }
            if (completed || !selectingControl) break;
            if (activation != controlActivation || controlRequest is not { } latest
                || latest.Window != window || version != controlVersion && hit is null) continue;
            Rectangle found = hit is { Bounds: var pixels }
                ? new Rectangle(pixels.X, pixels.Y, pixels.Width, pixels.Height) : Rectangle.Empty;
            if (version != controlVersion && !found.Contains(latest.Point)) continue;
            controlCandidate = !found.IsEmpty && CaptureGeometry.IsFullyCovered(found, SelectionMonitors)
                ? (observedWindow, found, hit!.Identity) : null;
            RefreshOverlays();
            break;
        }
    }

    private void UpdateControlShortcutMenu()
    {
        CaptureModifiers before = CaptureKeyMap.Combinations.FirstOrDefault(keys => OutputSettings.CaptureBeforeKeys.Resolve(keys) == CaptureKeyAction.SelectControl);
        CaptureModifiers during = CaptureKeyMap.Combinations.FirstOrDefault(keys => OutputSettings.CaptureKeys.Resolve(keys) == CaptureKeyAction.SelectControl);
        string label = string.Join("／", new[]
        {
            before == CaptureModifiers.None ? null : "選択前 " + CaptureKeyMap.Label(before),
            during == CaptureModifiers.None ? null : "ドラッグ中 " + CaptureKeyMap.Label(during)
        }.OfType<string>());
        string control = before != CaptureModifiers.None ? $"CaptureBeforeKeys{(int)before}"
            : during != CaptureModifiers.None ? $"CaptureKeys{(int)during}" : "CaptureBeforeKeysEditor";
        SelectionMenu.SetControlSelectionShortcut(label.Length == 0 ? "未割り当て" : label, control);
    }

    private void ExitControlSelection(bool restoreOverlayFocus = true)
    {
        if (!selectingControl) return;
        selectingControl = false;
        confirmingControl = false;
        controlActivation++;
        controlVersion++;
        controlRequest = null;
        controlCandidate = null;
        controlDrag = null;
        startingControl = null;
        controlDragLookup = null;
        if (controlPress)
        {
            suppressedButtons |= pressedButton;
            ReleasePointer();
        }
        controlPress = false;
        if (restoreOverlayFocus && CaptureNative.GetForegroundWindow() == focusBeforeSelection)
        {
            var overlay = dragOwner ?? overlays.FirstOrDefault(value => value.Visible && value.MonitorBounds.Contains(Pointer));
            overlay?.Activate();
            overlay?.Focus();
        }
        if (!completed) RefreshOverlays();
    }

    private void BeginControlPress(CaptureOverlay overlay)
    {
        controlPress = true;
        controlDragStart = Pointer;
        controlDragThreshold = CaptureNative.DragThreshold(overlay.DeviceDpi);
        startingControl = UsesControlPreview && controlCandidate is { } target && target.Bounds.Contains(Pointer)
            && findWindow?.Invoke(Pointer)?.Handle != target.Window.Handle
            ? new(target.Window, target.Bounds, target.Identity) : null;
        overlay.Capture = true;
    }

    private void MoveControlDrag(Point pointer)
    {
        if (!controlPress) return;
        if (controlDrag is null)
        {
            if (!SelectionGesture.ExceedsThreshold(controlDragStart, pointer, controlDragThreshold)) return;
            controlDrag = new(controlDragStart);
            controlRequest = null;
            ResetTargetMargin();
        }
        controlDrag.Move(pointer, findWindow, startingControl);
        startingControl = null;
        if (controlDragLookup is not { IsCompleted: false } && controlDrag.NextWindow is not null)
            controlDragLookup = ResolveControlDragAsync(controlDrag);
    }

    private async Task ResolveControlDragAsync(CaptureControlDrag drag)
    {
        if (controlLookup is { } hover) await hover;
        while (!completed && ReferenceEquals(controlDrag, drag) && drag.NextWindow is { } window)
        {
            CaptureWindow observed = window;
            ControlLayout? layout = null;
            try
            {
                Rectangle actual = CaptureNative.RequireCaptureWindow(window);
                if (!OutputSettings.FreezeFrame || window.Bounds == Rectangle.Intersect(actual, Snapshot.Bounds))
                {
                    observed = window with { Bounds = actual };
                    layout = await CaptureContextClient.Shared.FindControlLayoutAsync(new(window.Handle.ToInt64(), window.ProcessId,
                        PixelBounds.From(actual), null));
                }
            }
            catch (InvalidOperationException) { }
            if (completed || !ReferenceEquals(controlDrag, drag)) return;
            drag.SetLayout(window, observed, layout, SelectionMonitors);
            RefreshOverlays();
        }
    }

    private async void ConfirmControlDrag(CaptureControlDrag drag)
    {
        controlPress = false;
        ReleasePointer();
        confirmingControl = true;
        controlConfirmationError = null;
        try
        {
            RefreshOverlays();
            if (controlDragLookup is { } pending) await pending;
            if (completed || !ReferenceEquals(controlDrag, drag)) return;
            if (drag.LayoutUnavailable)
                throw new InvalidOperationException("ドラッグ経路のコントロール情報を取得できませんでした。選び直してください。");
            var targets = drag.Selected.ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("撮影できるコントロールが見つかりません。");
            foreach (var window in targets.Select(target => target.Window).DistinctBy(window => window.Handle))
                if (CaptureNative.RequireCaptureWindow(window) != window.Bounds)
                    throw new InvalidOperationException("対象の位置が変わりました。もう一度選び直してください。");

            var regions = targets.Select(target => target.Bounds).Distinct().ToArray();
            var group = new CaptureReplaySource.ControlGroup(targets, previewControl || UsesWindowSource);
            CompleteSelection(new(regions.Aggregate(Rectangle.Union), null, Destination,
                WindowTitle: $"コントロール {targets.Length} 件", Composite: new(Regions: regions), ReplaySource: group));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            if (!completed && ReferenceEquals(controlDrag, drag)) controlConfirmationError = ex.Message;
        }
        finally
        {
            if (!completed && ReferenceEquals(controlDrag, drag))
            {
                confirmingControl = false;
                controlDrag = null;
                controlDragLookup = null;
                controlCandidate = null;
                controlRequest = null;
                RefreshOverlays();
            }
        }
    }

    private async void ConfirmControl(Point pointer)
    {
        controlPress = false;
        ReleasePointer();
        confirmingControl = true;
        controlConfirmationError = null;
        int activation = controlActivation;
        try
        {
            RefreshOverlays();
            if (controlLookup is { } lookup) await lookup;
            if (!StillConfirming()) return;
            if (controlCandidate is not { } control || !control.Bounds.Contains(pointer))
                throw new InvalidOperationException("撮影できるコントロールが見つかりません。");
            bool isolated = UsesControlPreview || UsesWindowSource;
            if (isolated)
            {
                if (windowPreview is { } preview) await preview.Frame;
                if (!StillConfirming()) return;
                if (!ControlPreviewReady) throw new InvalidOperationException("コントロールのプレビューを取得できません。");
            }
            Rectangle current = CaptureNative.RequireCaptureWindow(control.Window);
            if (current != control.Window.Bounds || !current.Contains(control.Bounds))
                throw new InvalidOperationException("対象の位置が変わりました。もう一度選び直してください。");
            UpdateTargetMargin();
            var source = new CaptureReplaySource.Control(control.Window, control.Identity, isolated) { TargetMargin = AppliedTargetMargin };
            CompleteSelection(new(ControlBounds, null, Destination, ControlSource: source));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            if (StillConfirming()) controlConfirmationError = ex.Message;
        }
        finally
        {
            if (StillConfirming())
            {
                confirmingControl = false;
                if (controlConfirmationError is not null)
                {
                    controlCandidate = null;
                    controlRequest = null;
                    waitingForSecondPoint = false;
                    gesture.Reset();
                }
                RefreshOverlays();
            }
        }

        bool StillConfirming() => !completed && confirmingControl && controlActivation == activation;
    }
}
