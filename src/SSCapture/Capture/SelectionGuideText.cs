using SSCapture.Services;

namespace SSCapture.Capture;

internal static class SelectionGuideText
{
    internal static string Create(CaptureMode mode, CaptureIntent intent, Size? fixedSize, CaptureAnchor anchor,
        CaptureKeyMap keys, CaptureMouseMap mouse, bool dragging, bool spaceEnabled, bool canCrossMonitors,
        CaptureSelectionMethod selectionMethod = CaptureSelectionMethod.Drag,
        CaptureActivation controlActivation = CaptureActivation.Push, CaptureActivation windowActivation = CaptureActivation.Push,
        CaptureAnchorCycle spaceCycle = CaptureAnchorCycle.Legacy, bool registeredFrame = false, bool fixedWheelEnabled = true)
    {
        string esc = KeyDisplay.Key(Keys.Escape);
        var lines = new List<string>();
        bool rectangle = mode is CaptureMode.Rectangle or CaptureMode.Scroll;
        bool twoPoints = rectangle && selectionMethod == CaptureSelectionMethod.TwoClicks;
        MouseButtons[] buttons = [MouseButtons.Left, MouseButtons.Right, MouseButtons.Middle];
        string select = string.Join(" / ", buttons.Where(button => mouse.Before(button) == CaptureClickAction.Select).Select(CaptureMouseMap.Label));
        if (mode == CaptureMode.FixedRegion && fixedSize is Size size)
        {
            string name = registeredFrame ? "固定位置とサイズ" : "固定サイズ";
            lines.Add($"{name}　基準 {size.Width} × {size.Height} px　基点: {anchor.Label()}");
            string keysToConfirm = $"{(select.Length > 0 ? select + " / " : "")}Enter　確定";
            lines.Add(registeredFrame ? keysToConfirm : keysToConfirm + "　　矢印　1px　　Shift＋矢印　10px");
            if (fixedWheelEnabled) lines.Add("ホイール　撮影範囲の調整");
            if (spaceEnabled) lines.Add(SpaceOperation(spaceCycle == CaptureAnchorCycle.Legacy ? CaptureAnchorCycle.CornersAndCenter : spaceCycle));
        }
        else if (dragging && rectangle)
        {
            if (spaceEnabled) lines.Add(SpaceOperation(spaceCycle));
            if (keys.WheelAdjustRectangle) lines.Add("ホイール　撮影範囲の調整");
        }

        var clicks = new List<string>();
        if (!dragging)
        {
            foreach (var button in buttons)
            {
                string label = CaptureMouseMap.Label(button);
                switch (mouse.Before(button))
                {
                    case CaptureClickAction.Select:
                        if (twoPoints) clicks.Add($"{label}　始点を指定");
                        else if (mode is not (CaptureMode.FixedRegion or CaptureMode.Scroll)) clicks.Add($"{label}　ウィンドウ");
                        string drag = label.Replace("クリック", "ドラッグ", StringComparison.Ordinal);
                        if (rectangle && !twoPoints)
                            clicks.Add($"{drag}　{(mode == CaptureMode.Scroll ? "スクロールする領域を選択" : "範囲選択")}");
                        if (mode == CaptureMode.Freeform) clicks.Add($"{drag}　輪郭を囲む");
                        if (mode == CaptureMode.Window && intent == CaptureIntent.Screenshot) clicks.Add($"{drag}　複数ウィンドウを合成");
                        break;
                    case CaptureClickAction.Desktop when mode != CaptureMode.Scroll: clicks.Add($"{label}　画面全体"); break;
                    case CaptureClickAction.Menu: clicks.Add($"{label}　メニュー"); break;
                }
            }
            clicks.Add($"{esc}　撮影終了");
        }
        else
        {
            if (twoPoints && select.Length > 0) clicks.Add($"{select}　終点を指定");
            clicks.Add($"{CancelKeys(mouse)}　選択だけ中断");
        }
        if (mode != CaptureMode.FixedRegion) clicks.Add("矢印　1px移動");
        lines.AddRange(clicks.Chunk(2).Select(pair => string.Join("　　", pair)));

        var modifiers = new List<string>();
        foreach (var chord in CaptureKeyMap.Combinations)
        {
            CaptureKeyAction action = keys.Resolve(chord);
            if (mode == CaptureMode.Scroll) action = SessionController.ScrollSelectionAction(action);
            if (action == CaptureKeyAction.SelectControl && intent != CaptureIntent.Screenshot) continue;
            if (!Applies(action, mode, dragging, canCrossMonitors)) continue;
            string label = CaptureKeyMap.Label(chord);
            if (!dragging && action is CaptureKeyAction.HighlightWindow or CaptureKeyAction.SelectControl)
            {
                var activation = action == CaptureKeyAction.HighlightWindow ? windowActivation : controlActivation;
                modifiers.Add($"{label}{(activation == CaptureActivation.Push ? "を押しながら" : "を短押し")}　{CaptureKeyMap.Describe(action)}");
                if (action == CaptureKeyAction.SelectControl) modifiers.Add("コントロールをドラッグ　複数を合成");
            }
            else if (!dragging && chord == CaptureModifiers.Alt && action == CaptureKeyAction.Snap)
                modifiers.Add($"{label}を短押し　スナップのON/OFF");
            else if (action == CaptureKeyAction.SelectDesktop)
                modifiers.Add($"{label}を押しながら　デスクトップの選択");
            else if (action == CaptureKeyAction.CrossMonitor)
                modifiers.Add($"{label}を押しながら　別のモニターへ移動");
            else if (action == CaptureKeyAction.SelectControl)
                modifiers.Add($"{label}を押しながら　コントロールを選択");
            else
            {
                bool combinesCenterAndRatio = action == CaptureKeyAction.FromCenterWithAspectRatio
                    && chord == (CaptureModifiers.Ctrl | CaptureModifiers.Shift)
                    && keys.Ctrl == CaptureKeyAction.FromCenter && keys.Shift == CaptureKeyAction.LockAspectRatio;
                modifiers.Add($"{label}　{(combinesCenterAndRatio ? "両方を適用" : CaptureKeyMap.Describe(action))}");
            }
        }
        lines.AddRange(modifiers.Chunk(2).Select(pair => string.Join("　　", pair)));
        return string.Join('\n', lines);
    }

    private static string SpaceOperation(CaptureAnchorCycle cycle) => cycle switch
    {
        CaptureAnchorCycle.Corners => "Space　基点切替（四隅）",
        CaptureAnchorCycle.CornersAndCenter => "Space　基点切替（四隅・中心）",
        _ => "Space　基点切替（対角）"
    };

    internal static bool Applies(CaptureKeyAction action, CaptureMode mode, bool dragging, bool canCrossMonitors) => action switch
    {
        CaptureKeyAction.None => false,
        CaptureKeyAction.CrossMonitor => canCrossMonitors,
        CaptureKeyAction.SwitchAnchor => mode is CaptureMode.FixedRegion or CaptureMode.Rectangle or CaptureMode.Scroll,
        CaptureKeyAction.HighlightWindow or CaptureKeyAction.SelectDesktop => !dragging,
        CaptureKeyAction.LockAspectRatio or CaptureKeyAction.FromCenter or CaptureKeyAction.FromCenterWithAspectRatio
            or CaptureKeyAction.Grid or CaptureKeyAction.LockWidth or CaptureKeyAction.LockHeight => dragging && mode is CaptureMode.Rectangle or CaptureMode.Scroll,
        _ => true
    };

    private static string CancelKeys(CaptureMouseMap? mouse, MouseButtons activeButton = MouseButtons.None) => string.Join(" / ", new[] { "Esc" }.Concat(
        new[] { MouseButtons.Right, MouseButtons.Middle }
            .Where(button => button != activeButton && mouse?.During(button) == CaptureClickAction.Cancel).Select(CaptureMouseMap.Label)));

    internal static string CancelHint(CaptureMouseMap? mouse = null, MouseButtons activeButton = MouseButtons.None) =>
        CancelKeys(mouse, activeButton) + "で取り消し";
}
