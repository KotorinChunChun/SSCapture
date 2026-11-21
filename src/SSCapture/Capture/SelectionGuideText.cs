using SSCapture.Services;

namespace SSCapture.Capture;

internal static class SelectionGuideText
{
    internal static string Create(CaptureMode mode, CaptureIntent intent, Size? fixedSize, CaptureAnchor anchor,
        CaptureKeyMap keys, CaptureMouseMap mouse, bool dragging, bool spaceEnabled, bool canCrossMonitors,
        CaptureSelectionMethod selectionMethod = CaptureSelectionMethod.Drag)
    {
        string esc = KeyDisplay.Key(Keys.Escape);
        var lines = new List<string>();
        bool twoPoints = mode == CaptureMode.Rectangle && selectionMethod == CaptureSelectionMethod.TwoClicks;
        if (mode == CaptureMode.FixedRegion && fixedSize is Size size)
        {
            lines.Add($"固定サイズ {size.Width} × {size.Height} px　基点: {anchor.Label()}");
            lines.Add($"{(mouse.BeforeLeft ? "左クリック / " : "")}Enter　確定　　矢印　1px　　Shift＋矢印　10px");
            if (spaceEnabled) lines.Add("Space　基点切替（四隅・中心）");
        }
        else if (dragging && spaceEnabled && mode == CaptureMode.Rectangle)
            lines.Add("Space　始点と終点を入れ替える");

        var clicks = new List<string>();
        if (!dragging)
        {
            if (mouse.BeforeLeft)
            {
                if (twoPoints) clicks.Add("左クリック　始点を指定");
                else if (mode != CaptureMode.FixedRegion) clicks.Add("左クリック　ウィンドウ");
                if (mode == CaptureMode.Rectangle && !twoPoints) clicks.Add("左ドラッグ　範囲選択");
                if (mode == CaptureMode.Freeform) clicks.Add("左ドラッグ　輪郭を囲む");
                if (mode == CaptureMode.Window && intent == CaptureIntent.Screenshot) clicks.Add("左ドラッグ　複数ウィンドウを合成");
            }
            if (mouse.BeforeMiddle) clicks.Add("中央クリック　画面全体");
            if (mouse.BeforeRight) clicks.Add("右クリック　メニュー");
            clicks.Add($"{esc}　撮影終了");
        }
        else
        {
            if (twoPoints) clicks.Add("左クリック　終点を指定");
            clicks.Add($"{(mouse.DragRight ? "右クリック / " : "")}{esc}　選択だけ中断");
        }
        if (mode != CaptureMode.FixedRegion) clicks.Add("矢印　1px移動");
        lines.AddRange(clicks.Chunk(2).Select(pair => string.Join("　　", pair)));

        var modifiers = new List<string>();
        foreach (var chord in CaptureKeyMap.Combinations)
        {
            CaptureKeyAction action = keys.Resolve(chord);
            if (action == CaptureKeyAction.SelectControl && intent != CaptureIntent.Screenshot) continue;
            if (!Applies(action, mode, dragging, canCrossMonitors)) continue;
            string label = CaptureKeyMap.Label(chord);
            if (chord == CaptureModifiers.Win && action == CaptureKeyAction.HighlightWindow)
            {
                modifiers.Add($"{label}を短押し　ウィンドウ撮影に切り替え");
                if (canCrossMonitors) modifiers.Add($"{label}を押しながら　別のモニターへ移動");
            }
            else if (!dragging && chord == CaptureModifiers.Alt && action == CaptureKeyAction.Snap)
                modifiers.Add($"{label}を短押し　スナップのON/OFF");
            else if (action == CaptureKeyAction.CrossMonitor)
                modifiers.Add($"{label}を押しながら　別のモニターへ移動");
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

    internal static bool Applies(CaptureKeyAction action, CaptureMode mode, bool dragging, bool canCrossMonitors) => action switch
    {
        CaptureKeyAction.None => false,
        CaptureKeyAction.CrossMonitor => canCrossMonitors,
        CaptureKeyAction.SwitchAnchor => mode is CaptureMode.FixedRegion or CaptureMode.Rectangle,
        CaptureKeyAction.HighlightWindow => !dragging,
        CaptureKeyAction.LockAspectRatio or CaptureKeyAction.FromCenter or CaptureKeyAction.FromCenterWithAspectRatio
            or CaptureKeyAction.Grid or CaptureKeyAction.LockWidth or CaptureKeyAction.LockHeight => dragging && mode == CaptureMode.Rectangle,
        _ => true
    };

    internal static string CancelHint(bool rightClick) => rightClick ? "Esc / 右クリックで取り消し" : "Escで取り消し";
}
