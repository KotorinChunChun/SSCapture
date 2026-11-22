namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private (CaptureMode Mode, bool Isolated, CaptureModifiers Keys)? windowPushReturn;
    private (CaptureKeyAction Action, CaptureModifiers Keys, Point Pointer, long At)? pendingModeTap;
    private bool controlToggled;

    private CaptureKeyAction TakeBeforeModeTap(Point pointer, CaptureModifiers modifiers, CaptureKeyAction action, bool windowsTap)
    {
        if (HasPointerPress || gesture.IsPressed)
        {
            pendingModeTap = null;
            return CaptureKeyAction.None;
        }
        if (modifiers == CaptureModifiers.None)
        {
            var pending = pendingModeTap;
            pendingModeTap = null;
            if (windowsTap && OutputSettings.CaptureBeforeKeys.Win == CaptureKeyAction.HighlightWindow
                && OutputSettings.WindowActivation == CaptureActivation.Toggle)
                return CaptureKeyAction.HighlightWindow;
            return pending is { } tap && CaptureWindowsKey.IsStationaryTap(tap.Pointer, pointer, Environment.TickCount64 - tap.At, false)
                ? tap.Action : CaptureKeyAction.None;
        }
        if (pendingModeTap is { } previous && (previous.Keys != modifiers || previous.Pointer != pointer))
            pendingModeTap = null;
        if ((modifiers & ~activeModifiers) != 0 && (modifiers & activeModifiers) == activeModifiers
            && (action == CaptureKeyAction.SelectControl && OutputSettings.ControlActivation == CaptureActivation.Toggle
                || action == CaptureKeyAction.HighlightWindow && modifiers != CaptureModifiers.Win && OutputSettings.WindowActivation == CaptureActivation.Toggle))
            pendingModeTap = (action, modifiers, pointer, Environment.TickCount64);
        return CaptureKeyAction.None;
    }

    private void UpdateBeforeWindowMode(CaptureModifiers modifiers, CaptureKeyAction beforeAction, bool toggle)
    {
        if (desktopPress) return;
        if (windowPushReturn is { } original && original.Keys != modifiers)
        {
            if (HasPointerPress) AbortSelection();
            RestoreWindowPush();
        }
        if (HasPointerPress || gesture.IsPressed || suppressedButtons != MouseButtons.None) return;
        if (toggle) ToggleWindowCaptureMode(preferWgc: true);
        if (windowPushReturn is null && beforeAction == CaptureKeyAction.HighlightWindow
            && OutputSettings.WindowActivation == CaptureActivation.Push)
        {
            windowPushReturn = (Mode, exclusiveWindow, modifiers);
            ChangeMode(CaptureMode.Window, preferWgc: true);
        }
    }

    private void RestoreWindowPush()
    {
        if (windowPushReturn is not { } original) return;
        windowPushReturn = null;
        ChangeMode(original.Mode, original.Isolated);
    }
}
