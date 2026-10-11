namespace SSCapture.UI;

internal sealed class SelectionGuideForm : Form
{
    private readonly Action cancel;
    private readonly Func<Keys, bool>? handleKey;
    private readonly Action? modifiersChanged;
    internal SelectionGuideForm(Rectangle monitor, CaptureIntent intent, Action cancel, Size? fixedSize = null,
        Func<Keys, bool>? handleKey = null, CaptureAnchor fixedAnchor = CaptureAnchor.TopLeft, Action? modifiersChanged = null)
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
        ClientSize = fixedSize is null ? new Size(560, 90) : new Size(660, 104);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Math.Max(monitor.Left, monitor.Right - Width - 16), Math.Max(monitor.Top, monitor.Bottom - Height - 16));
        Controls.Add(new Label
        {
            Name = "SelectionHelp", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4),
            Text = fixedSize is Size dimensions
                ? $"固定サイズ {dimensions.Width} × {dimensions.Height} px　基点: {AnchorText(fixedAnchor)}　左クリック / Enter: 確定\n矢印: 1px移動　Shift＋矢印: 10px移動　Alt: 枠へスナップ（20px）\n右クリック: 出力先　Esc: キャンセル　画面外の枠は確定できません"
                : "左クリック: ウィンドウ　中央クリック: 画面全体\n左ドラッグ: 範囲選択　Alt: 矩形を枠へスナップ（20px）\n右クリック: 出力先　Esc: キャンセル"
        });
        FormClosing += (_, _) => cancel();
    }
    protected override bool ShowWithoutActivation => true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { cancel(); return true; }
        if (handleKey?.Invoke(keyData) == true) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void WndProc(ref Message m)
    {
        if (SSCapture.Capture.CaptureNative.IsAltTransition(m.Msg, m.WParam))
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
