using System.Drawing.Drawing2D;

namespace SSCapture.Capture;

internal sealed class CaptureOverlay : Form
{
    private readonly SessionController session;
    private readonly Font dimensionsFont = new("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private bool sessionNotifications;
    internal Rectangle MonitorBounds { get; }
    internal CaptureContextMenu SelectionMenu => session.SelectionMenu;

    internal CaptureOverlay(SessionController session, Rectangle monitorBounds)
    {
        this.session = session;
        MonitorBounds = monitorBounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = monitorBounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Text = WindowTitle.Create("範囲選択");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            // 範囲選択画面をFancyZones等の通常ウィンドウ自動配置から除外する。
            // ハンドル再作成時も維持し、キー入力に必要なアクティブ化は許可する。
            parameters.ExStyle |= CaptureNative.WsExToolWindow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        sessionNotifications = CaptureNative.WTSRegisterSessionNotification(Handle, 0);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (sessionNotifications)
        {
            CaptureNative.WTSUnRegisterSessionNotification(Handle);
            sessionNotifications = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FixPhysicalBounds();
    }

    private void FixPhysicalBounds()
    {
        if (IsHandleCreated && !IsDisposed)
            CaptureNative.SetWindowPos(Handle, new nint(-1), MonitorBounds.X, MonitorBounds.Y,
                MonitorBounds.Width, MonitorBounds.Height, 0x0010 /* SWP_NOACTIVATE */);
    }

    protected override void WndProc(ref Message m)
    {
        if (CaptureNative.IsModifierTransition(m.Msg, m.WParam))
        {
            session.AltKeyChanged();
            return;
        }
        // Alt is a selection modifier, so do not enter Windows' menu-key mode.
        if (m.Msg == CaptureNative.WmSysCommand && (m.WParam.ToInt64() & 0xfff0) == CaptureNative.ScKeyMenu) return;
        if (m.Msg == CaptureNative.WmDisplayChange ||
            (m.Msg == CaptureNative.WmSessionChange && (m.WParam.ToInt32() is 2 or 4 or 6 or 7 or 11)))
        {
            session.Cancel();
            return;
        }
        if (m.Msg == CaptureNative.WmActivateApp && m.WParam == 0)
        {
            session.ApplicationDeactivated();
            if (IsDisposed) return;
        }
        base.WndProc(ref m);
        // WinForms can apply WM_DPICHANGED's suggested (scaled) bounds even with AutoScale=None.
        // Reassert this monitor's physical bounds after its internal DPI bookkeeping.
        if (m.Msg == CaptureNative.WmDpiChanged) FixPhysicalBounds();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (session.HandleCommandKey(keyData, (msg.LParam.ToInt64() & (1L << 30)) != 0)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); session.MouseDown(this, e.Button, new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y)); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); session.Move(true, new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y)); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); session.MouseUp(e.Button, new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y)); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); session.CaptureLost(this); }
    protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); session.Cancel(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        DrawSnapshot(graphics);
        if (session.DimCaptureBackground || session.HighlightWindow)
            using (SolidBrush dim = new(Color.FromArgb(115, Color.Black))) graphics.FillRectangle(dim, ClientRectangle);
        if (session.HighlightWindow)
        {
            Rectangle target = session.HighlightedWindow;
            target.Offset(-MonitorBounds.X, -MonitorBounds.Y);
            if (target.Width > 0 && target.Height > 0) Reveal(graphics, target);
        }
        Rectangle selection = session.Selection;
        selection.Offset(-MonitorBounds.X, -MonitorBounds.Y);
        DrawSelection(graphics, selection);
        DrawDimensions(graphics, selection);
        DrawModifierHint(graphics, selection);
        if (session.SelectionError is string error)
        {
            var box = new Rectangle(16, 16, Math.Max(1, ClientSize.Width - 32), 100);
            graphics.FillRectangle(Brushes.White, box);
            TextRenderer.DrawText(graphics, error, dimensionsFont, box, Color.DarkRed, TextFormatFlags.WordBreak);
        }
    }

    private void DrawSelection(Graphics graphics, Rectangle selection)
    {
        using Pen border = new(session.FixedSelectionValid ? Color.FromArgb(55, 195, 255) : Color.OrangeRed, 2);
        if (session.Mode == CaptureMode.Freeform && session.Points.Count >= 2)
            DrawFreeformSelection(graphics, border);
        else if (selection.Width > 0 && selection.Height > 0)
        {
            if (!session.HighlightWindow) Reveal(graphics, selection);
            graphics.DrawRectangle(border, selection);
        }
    }

    private void DrawFreeformSelection(Graphics graphics, Pen border)
    {
        Point[] points = session.Points.Select(p => new Point(p.X - MonitorBounds.X, p.Y - MonitorBounds.Y)).ToArray();
        if (points.Length >= 3)
        {
            using GraphicsPath path = new(FillMode.Alternate);
            path.AddPolygon(points);
            if (!session.HighlightWindow)
            {
                GraphicsState state = graphics.Save();
                graphics.SetClip(path, CombineMode.Intersect);
                DrawSnapshot(graphics);
                graphics.Restore(state);
            }
            graphics.DrawPath(border, path);
        }
        else graphics.DrawLines(border, points);
    }

    private void DrawDimensions(Graphics graphics, Rectangle selection)
    {
        if (selection.Width <= 0 || selection.Height <= 0 || !MonitorBounds.Contains(session.Current)) return;
        string text = $"{selection.Width} × {selection.Height}" + (session.FixedSelectionValid ? "" : " 画面外");
        DrawPointerLabel(graphics, text, session.Current, 24, reserveHint: true);
    }

    private void DrawModifierHint(Graphics graphics, Rectangle selection)
    {
        if (session.ModifierHint is not string hint || !MonitorBounds.Contains(session.Pointer)) return;
        bool nearDimensions = selection.Width > 0 && selection.Height > 0
            && MonitorBounds.Contains(session.Current) && Math.Abs(session.Current.Y - session.Pointer.Y) < 52;
        DrawPointerLabel(graphics, hint, session.Pointer, nearDimensions ? 52 : 24, reserveHint: false);
    }

    private void DrawPointerLabel(Graphics graphics, string text, Point pointer, int offset, bool reserveHint)
    {
        Size size = TextRenderer.MeasureText(text, dimensionsFont);
        int height = size.Height + 4;
        int width = Math.Min(ClientSize.Width, size.Width + 8);
        int x = Math.Clamp(pointer.X - MonitorBounds.X + 18, 0, Math.Max(0, ClientSize.Width - width));
        int reserved = reserveHint && session.ModifierHint is not null ? height : 0;
        int y = Math.Clamp(pointer.Y - MonitorBounds.Y + offset, 0, Math.Max(0, ClientSize.Height - height - reserved));
        Rectangle label = new(x, y, width, height);
        graphics.FillRectangle(Brushes.Black, label);
        TextRenderer.DrawText(graphics, text, dimensionsFont, label, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void Reveal(Graphics graphics, Rectangle bounds)
    {
        GraphicsState state = graphics.Save();
        graphics.SetClip(bounds, CombineMode.Intersect);
        DrawSnapshot(graphics);
        graphics.Restore(state);
    }

    private void DrawSnapshot(Graphics graphics)
    {
        graphics.DrawImage(session.Snapshot.Image, new Rectangle(Point.Empty, MonitorBounds.Size),
            new Rectangle(MonitorBounds.X - session.Snapshot.Bounds.X, MonitorBounds.Y - session.Snapshot.Bounds.Y,
                MonitorBounds.Width, MonitorBounds.Height), GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) dimensionsFont.Dispose();
        base.Dispose(disposing);
    }
}
