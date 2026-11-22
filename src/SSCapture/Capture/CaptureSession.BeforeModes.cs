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
            if (pending is { } tap && CaptureWindowsKey.IsStationaryTap(tap.Pointer, pointer, Environment.TickCount64 - tap.At, false))
                return tap.Action;
            if (windowsTap && OutputSettings.CaptureBeforeKeys.Win == CaptureKeyAction.HighlightWindowToggle)
                return CaptureKeyAction.HighlightWindowToggle;
            return CaptureKeyAction.None;
        }
        if (pendingModeTap is { } previous && (previous.Pointer != pointer || (modifiers & previous.Keys) != modifiers))
            pendingModeTap = null;
        // A combination stays armed until the last of its keys is released.
        if ((modifiers & ~activeModifiers) != 0 && (modifiers & activeModifiers) == activeModifiers)
            pendingModeTap = CaptureKeyMap.IsModeToggle(action) && !(action == CaptureKeyAction.HighlightWindowToggle && modifiers == CaptureModifiers.Win)
                ? (action, modifiers, pointer, Environment.TickCount64) : null;
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
        if (windowPushReturn is null && beforeAction == CaptureKeyAction.HighlightWindowPush)
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
