namespace SSCapture.Capture;

internal enum CaptureClickAction { None, Select, Menu, Desktop, Cancel, ToggleMagnifier }
internal enum CaptureSingleClickAction { Window, Desktop, Control, None }

internal sealed record CaptureMouseMap(
    CaptureClickAction BeforeLeftAction = CaptureClickAction.Select,
    CaptureClickAction BeforeRightAction = CaptureClickAction.Menu,
    CaptureClickAction BeforeMiddleAction = CaptureClickAction.None,
    CaptureClickAction DragRightAction = CaptureClickAction.Cancel,
    CaptureClickAction DragMiddleAction = CaptureClickAction.None,
    CaptureSingleClickAction UnmodifiedLeftClick = CaptureSingleClickAction.Window)
{
    internal CaptureClickAction Before(MouseButtons button) => button switch
    {
        MouseButtons.Left => BeforeLeftAction,
        MouseButtons.Right => BeforeRightAction,
        MouseButtons.Middle => BeforeMiddleAction,
        _ => CaptureClickAction.None
    };

    internal CaptureClickAction During(MouseButtons button) => button switch
    {
        MouseButtons.Right => DragRightAction,
        MouseButtons.Middle => DragMiddleAction,
        _ => CaptureClickAction.None
    };

    internal bool IsValid => ValidBefore(BeforeLeftAction) && ValidBefore(BeforeRightAction)
        && ValidBefore(BeforeMiddleAction) && ValidDuring(DragRightAction) && ValidDuring(DragMiddleAction)
        && Enum.IsDefined(UnmodifiedLeftClick);

    internal static string SingleClickTarget(CaptureSingleClickAction action) => action switch
    {
        CaptureSingleClickAction.Window => "ウィンドウ",
        CaptureSingleClickAction.Desktop => "モニター",
        CaptureSingleClickAction.Control => "コントロール",
        _ => "なし"
    };

    internal const string MagnifierLabel = "拡大鏡を表示／非表示";

    internal static bool ValidBefore(CaptureClickAction action) =>
        action is CaptureClickAction.None or CaptureClickAction.Select or CaptureClickAction.Menu or CaptureClickAction.Desktop or CaptureClickAction.ToggleMagnifier;

    internal static bool ValidDuring(CaptureClickAction action) =>
        action is CaptureClickAction.None or CaptureClickAction.Cancel or CaptureClickAction.ToggleMagnifier;

    internal static string Label(MouseButtons button) => button switch
    {
        MouseButtons.Left => "左クリック",
        MouseButtons.Right => "右クリック",
        MouseButtons.Middle => "中クリック",
        _ => ""
    };
}
