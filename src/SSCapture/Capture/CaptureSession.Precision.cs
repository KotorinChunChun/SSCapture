namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private bool waitingForSecondPoint;
    private Point? precisePointer;
    internal bool TwoPointSelection => IsRectangleMode && OutputSettings.SelectionMethod == CaptureSelectionMethod.TwoClicks;

    internal void ChangeSelectionMethod(CaptureSelectionMethod method)
    {
        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
        if (completed || OutputSettings.SelectionMethod == method) return;
        AbortSelection();
        OutputSettings = OutputSettings with { SelectionMethod = method };
        UpdateMenu();
        ReplaceGuide();
        RefreshOverlays();
    }

    internal void ChangeMagnifierVisibility(bool enabled)
    {
        if (completed || OutputSettings.ShowMagnifier == enabled) return;
        OutputSettings = OutputSettings with { ShowMagnifier = enabled };
        UpdateMenu();
        RefreshOverlays();
    }

    internal void ChangeSharePresentation(bool enabled)
    {
        if (completed || OutputSettings.ShareCapturePresentation == enabled) return;
        OutputSettings = OutputSettings with { ShareCapturePresentation = enabled };
        guide?.SetShared(enabled);
        UpdateMenu();
        RefreshOverlays();
    }

    private void OnTwoPointClick()
    {
        ReleasePointer();
        if (!waitingForSecondPoint)
        {
            waitingForSecondPoint = true;
            gesture.Begin(Pointer, new Size(2, 2), lastSnap && precisePointer is null);
            RefreshOverlays();
            return;
        }
        if (gesture.ConfirmTwoPoint(Destination) is CaptureSelection selection)
            CompleteSelection(selection);
        else
            RefreshOverlays();
    }

    private bool NudgeCursor(Keys keys)
    {
        int step = Mode == CaptureMode.FixedRegion && (keys & Keys.Shift) != 0 ? 10 : 1;
        Size delta = (keys & Keys.KeyCode) switch
        {
            Keys.Left => new(-step, 0),
            Keys.Right => new(step, 0),
            Keys.Up => new(0, -step),
            Keys.Down => new(0, step),
            _ => Size.Empty
        };
        if (delta.IsEmpty || Mode == CaptureMode.FixedRegion && gesture.FixedPosition is not null) return false;
        if (!captureWindowsKey && Mode == CaptureMode.FixedRegion)
        {
            if (!gesture.Nudge(keys)) return false;
        }
        else
        {
            Point target = Pointer + delta;
            Point? actual = captureWindowsKey ? MovePointerToCorner(target) : target;
            if (actual is Point point)
            {
                precisePointer = point;
                ApplyPointerInput(point, activeModifiers, forceMove: true);
            }
        }
        hasSelectionInput = true;
        RefreshOverlays();
        return true;
    }
}
