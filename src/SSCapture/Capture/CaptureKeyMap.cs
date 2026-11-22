namespace SSCapture.Capture;

[Flags]
internal enum CaptureModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

internal enum CaptureKeyAction { None, LockWidth, LockHeight, Snap, CrossMonitor, HighlightWindow, SwitchAnchor, LockAspectRatio, FromCenter, FromCenterWithAspectRatio, Grid, SelectControl, SelectDesktop }

internal enum CaptureAnchorCycle { Legacy, Diagonal, Corners, CornersAndCenter }

internal sealed record CaptureKeyMap(
    CaptureKeyAction Ctrl = CaptureKeyAction.None,
    CaptureKeyAction Shift = CaptureKeyAction.None,
    CaptureKeyAction Alt = CaptureKeyAction.None,
    CaptureKeyAction Win = CaptureKeyAction.None,
    CaptureKeyAction CtrlShift = CaptureKeyAction.None,
    CaptureKeyAction CtrlAlt = CaptureKeyAction.None,
    CaptureKeyAction CtrlWin = CaptureKeyAction.None,
    CaptureKeyAction ShiftAlt = CaptureKeyAction.None,
    CaptureKeyAction ShiftWin = CaptureKeyAction.None,
    CaptureKeyAction AltWin = CaptureKeyAction.None,
    bool SpaceEnabled = false, CaptureAnchorCycle SpaceCycle = CaptureAnchorCycle.Legacy, bool WheelAdjustRectangle = true)
{
    internal static CaptureKeyMap BeforeDefaults => CaptureKeyDefaults.Current.Before;

    internal static bool IsBeforeAction(CaptureKeyAction action) => action is CaptureKeyAction.None or CaptureKeyAction.Snap
        or CaptureKeyAction.CrossMonitor or CaptureKeyAction.SwitchAnchor or CaptureKeyAction.HighlightWindow or CaptureKeyAction.SelectControl or CaptureKeyAction.SelectDesktop;

    internal static bool IsDragAction(CaptureKeyAction action) => Enum.IsDefined(action)
        && action is not (CaptureKeyAction.HighlightWindow or CaptureKeyAction.SelectDesktop);

    internal static IEnumerable<CaptureModifiers> Combinations =>
    [
        CaptureModifiers.Ctrl, CaptureModifiers.Shift, CaptureModifiers.Alt, CaptureModifiers.Win,
        CaptureModifiers.Ctrl | CaptureModifiers.Shift, CaptureModifiers.Ctrl | CaptureModifiers.Alt,
        CaptureModifiers.Ctrl | CaptureModifiers.Win, CaptureModifiers.Shift | CaptureModifiers.Alt,
        CaptureModifiers.Shift | CaptureModifiers.Win, CaptureModifiers.Alt | CaptureModifiers.Win
    ];

    internal CaptureKeyAction Resolve(CaptureModifiers keys) => keys switch
    {
        CaptureModifiers.Ctrl => Ctrl,
        CaptureModifiers.Shift => Shift,
        CaptureModifiers.Alt => Alt,
        CaptureModifiers.Win => Win,
        CaptureModifiers.Ctrl | CaptureModifiers.Shift => CtrlShift,
        CaptureModifiers.Ctrl | CaptureModifiers.Alt => CtrlAlt,
        CaptureModifiers.Ctrl | CaptureModifiers.Win => CtrlWin,
        CaptureModifiers.Shift | CaptureModifiers.Alt => ShiftAlt,
        CaptureModifiers.Shift | CaptureModifiers.Win => ShiftWin,
        CaptureModifiers.Alt | CaptureModifiers.Win => AltWin,
        _ => CaptureKeyAction.None
    };

    internal static string Describe(CaptureKeyAction action) => action switch
    {
        CaptureKeyAction.LockWidth => "上下に変更",
        CaptureKeyAction.LockHeight => "左右に変更",
        CaptureKeyAction.Snap => "ウィンドウスナップ",
        CaptureKeyAction.Grid => "一時グリッドスナップ",
        CaptureKeyAction.CrossMonitor => "別モニターへの移動を許可",
        CaptureKeyAction.HighlightWindow => "ウィンドウ撮影に切り替え",
        CaptureKeyAction.SwitchAnchor => "基点を切り替える",
        CaptureKeyAction.LockAspectRatio => "縦横比を固定",
        CaptureKeyAction.FromCenter => "起点を中心にする",
        CaptureKeyAction.FromCenterWithAspectRatio => "中心基準＋比率固定",
        CaptureKeyAction.SelectControl => "コントロールを選択",
        CaptureKeyAction.SelectDesktop => "デスクトップの選択",
        _ => "なし"
    };

    internal static string Label(CaptureModifiers keys) => string.Join("＋",
        new[] { CaptureModifiers.Win, CaptureModifiers.Ctrl, CaptureModifiers.Alt, CaptureModifiers.Shift }
            .Where(key => keys.HasFlag(key)).Select(key => key == CaptureModifiers.Win ? Services.KeyDisplay.Key(Keys.LWin) : key.ToString()));

    internal static CaptureModifiers FromKeys(Keys keys) =>
        ((keys & Keys.Control) != 0 ? CaptureModifiers.Ctrl : CaptureModifiers.None)
        | ((keys & Keys.Shift) != 0 ? CaptureModifiers.Shift : CaptureModifiers.None)
        | ((keys & Keys.Alt) != 0 ? CaptureModifiers.Alt : CaptureModifiers.None);
}
