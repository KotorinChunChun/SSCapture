using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private nint focusBeforeSelection;
    private bool selectingControl, controlPress, controlLookupRunning, controlHoldCancelled;
    private bool previewControl;
    private int controlVersion, controlActivation;
    private (Point Point, CaptureWindow? Window)? controlRequest;
    private (CaptureWindow Window, Rectangle Bounds, ControlIdentity? Identity)? controlCandidate;
    internal Rectangle ControlBounds => controlCandidate?.Bounds ?? Rectangle.Empty;
    internal bool SelectingControl => selectingControl;
    internal bool UsesControlPreview => selectingControl && previewControl;
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
            if (focusBeforeSelection != 0 && CaptureNative.OwnsForegroundWindow()
                && CaptureNative.SetForegroundWindow(focusBeforeSelection))
            {
                foreach (CaptureOverlay overlay in overlays)
                    if (overlay.Visible) overlay.RaiseWithoutActivation();
                if (guide?.Visible == true) CaptureNative.RaiseWithoutActivation(guide.Handle);
                if (dragOwner is not null && !dragOwner.Capture) dragOwner.Capture = true;
            }
        }
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
        if (!controlLookupRunning && window is not null) LookupControls();
    }

    private async void LookupControls()
    {
        controlLookupRunning = true;
        try
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
                if (version == controlVersion || found.Contains(latest.Point))
                {
                    controlCandidate = !found.IsEmpty && CaptureGeometry.IsFullyCovered(found, SelectionMonitors)
                        ? (observedWindow, found, hit!.Identity) : null;
                    RefreshOverlays();
                    break;
                }
            }
        }
        finally { controlLookupRunning = false; }
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
        controlActivation++;
        controlVersion++;
        controlRequest = null;
        controlCandidate = null;
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

    private void ConfirmControl(Point pointer)
    {
        var candidate = controlCandidate;
        controlPress = false;
        ReleasePointer();
        if (candidate is { } control && control.Bounds.Contains(pointer))
        {
            try
            {
                if (UsesControlPreview && !ControlPreviewReady) return;
                Rectangle current = CaptureNative.RequireCaptureWindow(control.Window);
                if (current != control.Window.Bounds || !current.Contains(control.Bounds))
                    throw new InvalidOperationException();
                var source = new CaptureReplaySource.Control(control.Window, control.Identity, UsesControlPreview || UsesWindowSource);
                CompleteSelection(new(control.Bounds, null, Destination, ControlSource: source));
                return;
            }
            catch (InvalidOperationException) { controlCandidate = null; }
        }
        waitingForSecondPoint = false;
        gesture.Reset();
        RefreshOverlays();
    }
}
