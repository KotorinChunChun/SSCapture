namespace SSCapture.UI;

internal sealed class SelectionGuideForm : Form
{
    private readonly Action cancel;
    private readonly Func<Keys, bool>? handleKey;
    private readonly Action? modifiersChanged;
    private readonly Action? dismiss;
    private readonly Rectangle monitor;
    private CaptureAnchor currentAnchor;
    internal SelectionGuideForm(Rectangle monitor, CaptureIntent intent, Action cancel, Size? fixedSize = null,
        Func<Keys, bool>? handleKey = null, CaptureAnchor fixedAnchor = CaptureAnchor.TopLeft, Action? modifiersChanged = null,
        CaptureMode mode = CaptureMode.Rectangle, Action? dismiss = null)
    {
        this.cancel = cancel;
        this.monitor = monitor;
        this.handleKey = handleKey;
        this.modifiersChanged = modifiersChanged;
        this.dismiss = dismiss;
        currentAnchor = fixedAnchor;
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
                ? $"固定サイズ {dimensions.Width} × {dimensions.Height} px　基点: {AnchorText(fixedAnchor)}　左クリック / Enter: 確定\nSpace: 基点を時計回り　矢印: 1px　Shift＋矢印: 10px　Alt: 枠吸着\n中央クリック: 画面全体　右クリック: メニュー　Esc: 撮影終了\nドラッグ中の右クリック / Esc: 選択だけ中断　画面外の枠は確定不可"
                : mode == CaptureMode.Rectangle
                    ? "左クリック: ウィンドウ　中央クリック: 画面全体　左ドラッグ: 範囲選択\nShift: 高さ固定　Ctrl: 幅固定　Alt: 20px単位＋枠吸着\nSpace: 現在位置を新しい始点に　右クリック: メニュー・縦横比\nEsc: 撮影終了　ドラッグ中の右クリック / Esc: 選択だけ中断"
                    : $"左クリック: ウィンドウ　中央クリック: 画面全体\n{(mode == CaptureMode.Freeform ? "左ドラッグ: 輪郭を囲む" : "ウィンドウを選んで左クリックで確定")}\n右クリック: メニュー　Esc: 撮影終了\nドラッグ中の右クリック / Esc: 選択だけ中断"
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (dismiss is not null) dismiss(); else Hide();
        }
        base.OnFormClosing(e);
    }

    internal static bool ShouldHideForPointer(Rectangle bounds, Point pointer, bool dragging, int dpi, bool hidden)
    {
        if (!dragging) return false;
        int margin = (int)Math.Ceiling((hidden ? 40 : 24) * Math.Max(96, dpi) / 96.0);
        bounds.Inflate(margin, margin);
        return bounds.Contains(pointer);
    }

    internal void UpdatePointer(Point pointer, bool dragging)
    {
        if (IsDisposed || Disposing) return;
        Rectangle next = AvoidPointer(Bounds, monitor, pointer, DeviceDpi);
        if (next == Bounds) return;
        // Hide/Showを使わず、選択中の入力・アクティブ状態を維持する。
        Location = next.Location;
    }

    internal static Rectangle AvoidPointer(Rectangle bounds, Rectangle monitor, Point pointer, int dpi)
    {
        int margin = (int)Math.Ceiling(40 * Math.Max(96, dpi) / 96d);
        if (!Rectangle.Inflate(bounds, margin, margin).Contains(pointer)) return bounds;
        int inset = (int)Math.Ceiling(16 * Math.Max(96, dpi) / 96d);
        int left = monitor.Left + inset, top = monitor.Top + inset;
        int right = Math.Max(monitor.Left, monitor.Right - bounds.Width - inset);
        int bottom = Math.Max(monitor.Top, monitor.Bottom - bounds.Height - inset);
        Rectangle[] candidates = [new(left, top, bounds.Width, bounds.Height), new(right, top, bounds.Width, bounds.Height),
            new(left, bottom, bounds.Width, bounds.Height), new(right, bottom, bounds.Width, bounds.Height)];
        double Distance(Rectangle r)
        {
            double x = Math.Max(Math.Max((double)r.Left - pointer.X, 0), (double)pointer.X - r.Right);
            double y = Math.Max(Math.Max((double)r.Top - pointer.Y, 0), (double)pointer.Y - r.Bottom);
            return x * x + y * y;
        }
        return candidates.Where(monitor.Contains).OrderByDescending(Distance).FirstOrDefault(bounds);
    }

    internal void ShowSaveError(string message) => Controls["SelectionHelp"]!.Text =
        "案内の表示設定を保存できませんでした。撮影は継続できます。\n" + message;
    internal void UpdateAnchor(CaptureAnchor anchor)
    {
        Controls["SelectionHelp"]!.Text = Controls["SelectionHelp"]!.Text.Replace("基点: " + AnchorText(currentAnchor), "基点: " + AnchorText(anchor));
        currentAnchor = anchor;
    }
    protected override bool ShowWithoutActivation => true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Space && (keyData & Keys.Alt) == 0 && (msg.LParam.ToInt64() & (1L << 30)) != 0) return true;
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
        if (m.Msg == 0x001C && m.WParam == 0 && Visible && !Disposing) cancel();
    }

    private static string AnchorText(CaptureAnchor anchor) => anchor switch
    {
        CaptureAnchor.TopLeft => "左上", CaptureAnchor.TopRight => "右上",
        CaptureAnchor.BottomRight => "右下", CaptureAnchor.BottomLeft => "左下",
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };
}
