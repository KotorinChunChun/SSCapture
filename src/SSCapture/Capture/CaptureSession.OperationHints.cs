namespace SSCapture.Capture;

// ポインタ横の操作ガイドに出す操作ヒント。撮影対象の説明はCaptureSession.TargetHints.csで組み立てる。
internal sealed partial class SessionController
{
    internal string? PointerHint
    {
        get
        {
            if (!OutputSettings.ShowPointerGuide || PointerLabelsSuppressed) return null;
            string hint = string.Join('\n', CaptureTargetHints().Prepend(OperationHint).Append(SelectionMarginHint).OfType<string>());
            return hint.Length == 0 ? null : hint;
        }
    }

    private string? OperationHint =>
        HasControlDrag ? ControlDragHint()
        : SelectingControl ? ControlSelectionHint()
        : TwoPointSelection && !SelectingDesktop ? TwoPointHint()
        : desktopHeld && !HasPointerPress ? $"{ModifierHint}　{SelectionGuideText.CancelHint()}"
        : !HasPointerPress ? IdleHint()
        : PressedHint();

    private string ControlDragHint()
    {
        string status = confirmingControl ? "コントロールを取得中" : $"ドラッグ：複数コントロールを合成（{SelectedControlCount} 件）";
        return $"{status}　{SelectionGuideText.CancelHint(OutputSettings.CaptureMouse, pressedButton)}";
    }

    private string ControlSelectionHint()
    {
        string controlOperation = controlConfirmationError ?? (confirmingControl ? "コントロールを取得中"
            : ControlBounds.IsEmpty ? FrozenScreenshot ? FrozenCaptureTargets.ControlUnavailable : "対象のコントロールが見つかりません"
            : UsesControlPreview && !ControlPreviewReady ? windowPreview?.Frame.IsFaulted == true
                ? "コントロールのプレビューを取得できません" : "コントロールをプレビュー中"
            : "ボタンを離してコントロールを撮影");
        return (ModifierHint is null ? "" : ModifierHint + "\n") + controlOperation + "　" +
            SelectionGuideText.CancelHint(OutputSettings.CaptureMouse, pressedButton) + "\nドラッグ：複数コントロールを合成";
    }

    private string TwoPointHint()
    {
        string step = waitingForSecondPoint ? "終点をクリック" : "始点をクリック";
        string hint = ModifierHint is null ? step : $"{ModifierHint}\n{step}";
        return $"{hint}　{SelectionGuideText.CancelHint(waitingForSecondPoint ? OutputSettings.CaptureMouse : null)}";
    }

    private string? IdleHint()
    {
        string? idleHint = controlConfirmationError ?? ModifierHint ?? (windowPreview?.Frame switch
        {
            { IsFaulted: true } => windowPreview.Frame.Exception?.GetBaseException().Message,
            { IsCompleted: false } => "ウィンドウのプレビューを取得中",
            _ => null
        });
        return idleHint is null ? null : $"{idleHint}　{SelectionGuideText.CancelHint()}";
    }

    private string PressedHint()
    {
        string operation = SelectingDesktop ? "モニター全体を撮影"
            : Mode == CaptureMode.Scroll ? "スクロールする領域を選択"
            : Mode == CaptureMode.FixedRegion ? "固定範囲を撮影"
            : !gesture.HasDragged || Mode == CaptureMode.Window ? "ウィンドウを撮影" : "選択範囲を撮影";
        string cancel = SelectionGuideText.CancelHint(OutputSettings.CaptureMouse, pressedButton);
        if (FreeDesktopDrag) return $"{operation}　{cancel}\nドラッグ：複数デスクトップを合成";
        if (HighlightWindow && !SelectingDesktop && IsScreenshot)
            return $"{operation}　{cancel}\nドラッグ：複数ウィンドウを合成";
        string effect = gesture.HasDragged ? ModifierHint ?? operation : operation;
        return $"{effect}　{cancel}";
    }
}
