namespace SSCapture.UI;

internal sealed class SelectionGuideForm : Form
{
    private readonly Action cancel;
    private readonly Func<Keys, bool>? handleKey;
    private readonly Action? modifiersChanged;
    internal SelectionGuideForm(Rectangle monitor, CaptureIntent intent, Action cancel, Size? fixedSize = null,
        Func<Keys, bool>? handleKey = null, CaptureAnchor fixedAnchor = CaptureAnchor.TopLeft, Action? modifiersChanged = null,
        CaptureMode mode = CaptureMode.Rectangle)
    {
        this.cancel = cancel;
        this.handleKey = handleKey;
        this.modifiersChanged = modifiersChanged;
        Name = "SelectionGuide";
        Text = WindowTitle.Create(intent == CaptureIntent.Recording ? "録画範囲の選択" : "撮影範囲の選択");
        Font = new Font("Yu Gothic UI", 11F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 116);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Math.Max(monitor.Left, monitor.Right - Width - 16), Math.Max(monitor.Top, monitor.Bottom - Height - 16));
        Controls.Add(new Label
        {
            Name = "SelectionHelp", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4),
            Text = fixedSize is Size dimensions
                ? $"固定サイズ {dimensions.Width} × {dimensions.Height} px　基点: {AnchorText(fixedAnchor)}　左クリック / Enter: 確定\n矢印: 1px移動　Shift＋矢印: 10px移動　Alt: 枠へスナップ\n中央クリック: 画面全体　右クリック: メニュー　Esc: 撮影終了\nドラッグ中の右クリック / Esc: 選択だけ中断　画面外の枠は確定不可"
                : mode == CaptureMode.Rectangle
                    ? "左クリック: ウィンドウ　中央クリック: 画面全体　左ドラッグ: 範囲選択\nShift: 高さ固定　Ctrl: 幅固定　Alt: 20px単位＋枠吸着\n右クリック: メニュー　Esc: 撮影終了\nドラッグ中の右クリック / Esc: 選択だけ中断"
                    : $"左クリック: ウィンドウ　中央クリック: 画面全体\n{(mode == CaptureMode.Freeform ? "左ドラッグ: 輪郭を囲む" : "ウィンドウを選んで左クリックで確定")}\n右クリック: メニュー　Esc: 撮影終了\nドラッグ中の右クリック / Esc: 選択だけ中断"
        });
        FormClosing += (_, _) => cancel();
    }
    protected override bool ShowWithoutActivation => true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (handleKey?.Invoke(keyData) == true) return true;
        if (keyData == Keys.Escape) { cancel(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void WndProc(ref Message m)
    {
        if (SSCapture.Capture.CaptureNative.IsModifierTransition(m.Msg, m.WParam))
        {
            modifiersChanged?.Invoke();
            return;
        }
        if (m.Msg == SSCapture.Capture.CaptureNative.WmSysCommand && (m.WParam.ToInt64() & 0xfff0) == SSCapture.Capture.CaptureNative.ScKeyMenu) return;
        base.WndProc(ref m);
        if (m.Msg == 0x001C && m.WParam == 0) cancel();
    }

    private static string AnchorText(CaptureAnchor anchor) => anchor switch
    {
        CaptureAnchor.TopLeft => "左上", CaptureAnchor.TopRight => "右上",
        CaptureAnchor.BottomRight => "右下", CaptureAnchor.BottomLeft => "左下",
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };
}
