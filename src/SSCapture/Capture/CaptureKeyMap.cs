namespace SSCapture.Capture;

[Flags]
internal enum CaptureModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

internal enum CaptureKeyAction { None, LockWidth, LockHeight, Snap, CrossMonitor, HighlightWindow, SwitchAnchor }

internal sealed record CaptureKeyMap(
    CaptureKeyAction Ctrl = CaptureKeyAction.LockWidth,
    CaptureKeyAction Shift = CaptureKeyAction.LockHeight,
    CaptureKeyAction Alt = CaptureKeyAction.Snap,
    CaptureKeyAction Win = CaptureKeyAction.HighlightWindow,
    CaptureKeyAction CtrlShift = CaptureKeyAction.CrossMonitor,
    CaptureKeyAction CtrlAlt = CaptureKeyAction.None,
    CaptureKeyAction CtrlWin = CaptureKeyAction.None,
    CaptureKeyAction ShiftAlt = CaptureKeyAction.None,
    CaptureKeyAction ShiftWin = CaptureKeyAction.None,
    CaptureKeyAction AltWin = CaptureKeyAction.None,
    bool SpaceEnabled = true)
{
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
        CaptureKeyAction.Snap => "ウィンドウ枠スナップ",
        CaptureKeyAction.CrossMonitor => "別モニタへの移動を許可",
        CaptureKeyAction.HighlightWindow => "ポインタ下のウィンドウを強調／解除",
        CaptureKeyAction.SwitchAnchor => "基点を切り替える",
        _ => "なし"
    };

    internal static string Label(CaptureModifiers keys) => string.Join("＋",
        Enum.GetValues<CaptureModifiers>().Where(key => key != CaptureModifiers.None && keys.HasFlag(key)));

    internal static CaptureModifiers FromKeys(Keys keys) =>
        ((keys & Keys.Control) != 0 ? CaptureModifiers.Ctrl : CaptureModifiers.None)
        | ((keys & Keys.Shift) != 0 ? CaptureModifiers.Shift : CaptureModifiers.None)
        | ((keys & Keys.Alt) != 0 ? CaptureModifiers.Alt : CaptureModifiers.None);
}
