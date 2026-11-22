namespace SSCapture.Capture;

[Flags]
internal enum CaptureModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

internal enum CaptureKeyAction { None, LockWidth, LockHeight, Snap, CrossMonitor, HighlightWindow, SwitchAnchor, LockAspectRatio, FromCenter, FromCenterWithAspectRatio, Grid, SelectControl, SelectDesktop, SelectControlPush, SelectControlToggle, HighlightWindowPush, HighlightWindowToggle }

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
        or CaptureKeyAction.CrossMonitor or CaptureKeyAction.SwitchAnchor or CaptureKeyAction.SelectDesktop
        || IsControlSelection(action) || IsWindowSelection(action);

    internal static bool IsDragAction(CaptureKeyAction action) => Enum.IsDefined(action)
        && !IsWindowSelection(action)
        && action is not (CaptureKeyAction.SelectDesktop or CaptureKeyAction.SelectControlPush or CaptureKeyAction.SelectControlToggle);

    internal static bool IsControlSelection(CaptureKeyAction action) => action is CaptureKeyAction.SelectControl
        or CaptureKeyAction.SelectControlPush or CaptureKeyAction.SelectControlToggle;

    internal static bool IsWindowSelection(CaptureKeyAction action) => action is CaptureKeyAction.HighlightWindow
        or CaptureKeyAction.HighlightWindowPush or CaptureKeyAction.HighlightWindowToggle;

    internal static bool IsModeToggle(CaptureKeyAction action) =>
        action is CaptureKeyAction.SelectControlToggle or CaptureKeyAction.HighlightWindowToggle;

    internal static CaptureKeyAction BeforeActivation(CaptureKeyAction action, CaptureActivation control, CaptureActivation window) => action switch
    {
        CaptureKeyAction.SelectControl => control == CaptureActivation.Toggle ? CaptureKeyAction.SelectControlToggle : CaptureKeyAction.SelectControlPush,
        CaptureKeyAction.HighlightWindow => window == CaptureActivation.Toggle ? CaptureKeyAction.HighlightWindowToggle : CaptureKeyAction.HighlightWindowPush,
        _ => action
    };

    internal CaptureKeyMap WithBeforeActivations(CaptureActivation control, CaptureActivation window)
    {
        CaptureKeyAction Resolve(CaptureKeyAction action) => BeforeActivation(action, control, window);
        return this with
        {
            Ctrl = Resolve(Ctrl), Shift = Resolve(Shift), Alt = Resolve(Alt), Win = Resolve(Win),
            CtrlShift = Resolve(CtrlShift), CtrlAlt = Resolve(CtrlAlt), CtrlWin = Resolve(CtrlWin),
            ShiftAlt = Resolve(ShiftAlt), ShiftWin = Resolve(ShiftWin), AltWin = Resolve(AltWin)
        };
    }

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
        CaptureKeyAction.LockWidth => "高さだけ変更",
        CaptureKeyAction.LockHeight => "幅だけ変更",
        CaptureKeyAction.Snap => "ウィンドウスナップ",
        CaptureKeyAction.Grid => "一時グリッドスナップ",
        CaptureKeyAction.CrossMonitor => "別モニターへの移動を許可",
        CaptureKeyAction.HighlightWindow => "ウィンドウ撮影に切り替え",
        CaptureKeyAction.SwitchAnchor => "基点を切り替える",
        CaptureKeyAction.LockAspectRatio => "縦横比を固定",
        CaptureKeyAction.FromCenter => "起点を中心にする",
        CaptureKeyAction.FromCenterWithAspectRatio => "中心基準＋比率固定",
        CaptureKeyAction.SelectControl => "コントロールを選択",
        CaptureKeyAction.SelectControlPush => "コントロール選択／プッシュ",
        CaptureKeyAction.SelectControlToggle => "コントロール選択／トグル",
        CaptureKeyAction.HighlightWindowPush => "ウィンドウ撮影／プッシュ",
        CaptureKeyAction.HighlightWindowToggle => "ウィンドウ撮影／トグル",
        CaptureKeyAction.SelectDesktop => "モニター全体を選択",
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
