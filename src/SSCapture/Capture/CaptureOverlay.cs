using System.Drawing.Drawing2D;

namespace SSCapture.Capture;

internal sealed class CaptureOverlay : Form
{
    private readonly SessionController session;
    private readonly Font dimensionsFont = new("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private bool sessionNotifications;
    private VisualState? rendered;
    internal Rectangle MonitorBounds { get; }
    internal CaptureContextMenu SelectionMenu => session.SelectionMenu;

    private sealed record VisualState(DesktopSnapshot Snapshot, CaptureMode Mode, bool Dim, bool Highlight,
        Rectangle Selection, Rectangle Target, WindowCaptureFrame? Preview, bool Valid, int Points, Rectangle Dimensions, Rectangle Hint, string? HintText,
        string? Error, int TrailPoints, Rectangle TrailBounds, IReadOnlyList<Rectangle> DragHighlights);

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
        _ = CaptureNative.SetWindowDisplayAffinity(Handle, CaptureNative.ExcludeFromCapture);
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
        // WinFormsは別ボタンのUpでもマウスキャプチャを解放するため、未割当の操作は渡さない。
        if ((session.Dragging && m.Msg is >= 0x0207 and <= 0x0209)
            || (session.HasPointerPress && m.Msg is >= 0x0204 and <= 0x0206 && !session.OutputSettings.CaptureMouse.DragRight)) return;
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
        Rectangle dirty = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
        DrawSnapshot(graphics, dirty);
        Rectangle target = session.HighlightForOverlay(MonitorBounds);
        if (session.DimCaptureBackground)
            using (SolidBrush dim = new(Color.FromArgb(115, Color.Black))) graphics.FillRectangle(dim, dirty);
        Rectangle bright = Rectangle.Intersect(Local(target), dirty);
        if (session.PreviewFrame is { } frame && bright.Width > 0 && bright.Height > 0)
        {
            if (session.DimCaptureBackground) graphics.FillRectangle(Brushes.Black, bright);
            Rectangle source = bright;
            source.Offset(MonitorBounds.X - frame.Bounds.X, MonitorBounds.Y - frame.Bounds.Y);
            graphics.DrawImage(frame.Image, bright, source, GraphicsUnit.Pixel);
        }
        else if (session.DimCaptureBackground) DrawSnapshot(graphics, bright);
        foreach (Rectangle window in session.WindowDragHighlights)
            DrawSnapshot(graphics, Rectangle.Intersect(Local(window), dirty));
        Rectangle selection = session.DisplaySelection;
        selection.Offset(-MonitorBounds.X, -MonitorBounds.Y);
        DrawSelection(graphics, selection, dirty);
        DrawWindowTrail(graphics, dirty);
        DrawDimensions(graphics, selection);
        DrawPointerHint(graphics, selection);
        if (session.SelectionError is string error)
        {
            var box = new Rectangle(16, 16, Math.Max(1, ClientSize.Width - 32), 100);
            graphics.FillRectangle(Brushes.White, box);
            TextRenderer.DrawText(graphics, error, dimensionsFont, box, Color.DarkRed,
                TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
        }
        if (rendered is null || dirty.Contains(ClientRectangle)) rendered = ReadVisualState();
    }

    internal bool InvalidateChanges()
    {
        var next = ReadVisualState();
        if (rendered is not { } old || old.Snapshot != next.Snapshot || old.Mode != next.Mode
            || old.Dim != next.Dim || old.Highlight != next.Highlight || old.Error != next.Error)
        {
            rendered = next;
            Invalidate();
            return true;
        }
        if (old == next) return false;
        using var dirty = new Region(Rectangle.Empty);
        if (next.Mode == CaptureMode.Freeform && old.Points != next.Points)
        {
            dirty.Union(Inflate(old.Selection));
            dirty.Union(Inflate(next.Selection));
        }
        else if (old.Selection != next.Selection || old.Valid != next.Valid)
        {
            if (!next.Highlight)
            {
                using var change = new Region(Local(old.Selection));
                change.Xor(Local(next.Selection));
                dirty.Union(change);
            }
            AddBorder(old.Selection);
            AddBorder(next.Selection);
        }
        if (old.Target != next.Target)
        {
            using var change = new Region(Local(old.Target));
            change.Xor(Local(next.Target));
            dirty.Union(change);
        }
        if (old.Preview != next.Preview)
        {
            if (old.Preview is not null) dirty.Union(Local(old.Target));
            if (next.Preview is not null) dirty.Union(Local(next.Target));
        }
        if (old.DragHighlights != next.DragHighlights)
        {
            foreach (Rectangle window in old.DragHighlights.Except(next.DragHighlights)) dirty.Union(Local(window));
            foreach (Rectangle window in next.DragHighlights.Except(old.DragHighlights)) dirty.Union(Local(window));
        }
        if (old.TrailPoints > next.TrailPoints) dirty.Union(Inflate(old.TrailBounds));
        else if (old.TrailPoints < next.TrailPoints)
        {
            var path = session.WindowDragPath;
            for (int index = Math.Max(1, old.TrailPoints); index < path.Count; index++)
                dirty.Union(Inflate(SegmentBounds(path[index - 1], path[index])));
        }
        if (old.Dimensions != next.Dimensions || old.Selection.Size != next.Selection.Size || old.Valid != next.Valid)
        {
            dirty.Union(old.Dimensions);
            dirty.Union(next.Dimensions);
        }
        if (old.Hint != next.Hint || old.HintText != next.HintText)
        {
            dirty.Union(old.Hint);
            dirty.Union(next.Hint);
        }
        dirty.Intersect(ClientRectangle);
        using var identity = new Matrix();
        rendered = next;
        if (dirty.GetRegionScans(identity).Length == 0) return false;
        Invalidate(dirty);
        return true;

        Rectangle Inflate(Rectangle bounds) => bounds.IsEmpty ? Rectangle.Empty : Rectangle.Inflate(Local(bounds), 3, 3);
        void AddBorder(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            Rectangle local = Local(bounds);
            using var border = new Region(Rectangle.Inflate(local, 3, 3));
            Rectangle interior = Rectangle.Inflate(local, -3, -3);
            if (interior.Width > 0 && interior.Height > 0) border.Exclude(interior);
            dirty.Union(border);
        }
    }

    private VisualState ReadVisualState()
    {
        Rectangle selection = session.DisplaySelection;
        string? dimensions = DimensionsText(selection);
        string? hint = MonitorBounds.Contains(session.Pointer) ? session.PointerHint : null;
        Rectangle target = session.HighlightForOverlay(MonitorBounds);
        return new(session.Snapshot, session.Mode, session.DimCaptureBackground, session.HighlightWindow, selection,
            target, session.PreviewFrame, session.FixedSelectionValid, session.Points.Count,
            dimensions is null ? Rectangle.Empty : PointerLabelBounds(dimensions, session.Current, 24, reserveHint: true),
            hint is null ? Rectangle.Empty : PointerLabelBounds(hint, session.Pointer, HintOffset(selection), reserveHint: false),
            hint, session.SelectionError, session.WindowDragPath.Count, session.WindowDragBounds, session.WindowDragHighlights);
    }

    private static Rectangle SegmentBounds(Point start, Point end) => Rectangle.FromLTRB(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X) + 1, Math.Max(start.Y, end.Y) + 1);

    private void DrawWindowTrail(Graphics graphics, Rectangle dirty)
    {
        var path = session.WindowDragPath;
        if (path.Count < 2) return;
        Point origin = MonitorBounds.Location;
        using var border = new Pen(Color.FromArgb(55, 195, 255), 2);
        for (int index = 1; index < path.Count; index++)
        {
            Point start = path[index - 1], end = path[index];
            if (!Rectangle.Inflate(Local(SegmentBounds(start, end)), 3, 3).IntersectsWith(dirty)) continue;
            graphics.DrawLine(border, start.X - origin.X, start.Y - origin.Y, end.X - origin.X, end.Y - origin.Y);
        }
    }

    private Rectangle Local(Rectangle screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return Rectangle.Empty;
        screen.Offset(-MonitorBounds.X, -MonitorBounds.Y);
        return screen;
    }

    private void DrawSelection(Graphics graphics, Rectangle selection, Rectangle dirty)
    {
        using Pen border = new(session.FixedSelectionValid ? Color.FromArgb(55, 195, 255) : Color.OrangeRed, 2);
        if (session.Mode == CaptureMode.Freeform && session.Points.Count >= 2)
            DrawFreeformSelection(graphics, border, dirty);
        else if (selection.Width > 0 && selection.Height > 0)
        {
            if (!session.HighlightWindow) DrawSnapshot(graphics, Rectangle.Intersect(selection, dirty));
            graphics.DrawRectangle(border, selection);
        }
    }

    private void DrawFreeformSelection(Graphics graphics, Pen border, Rectangle dirty)
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
                DrawSnapshot(graphics, dirty);
                graphics.Restore(state);
            }
            graphics.DrawPath(border, path);
        }
        else graphics.DrawLines(border, points);
    }

    private void DrawDimensions(Graphics graphics, Rectangle selection)
    {
        if (DimensionsText(selection) is string text)
            DrawPointerLabel(graphics, text, session.Current, 24, reserveHint: true);
    }

    private string? DimensionsText(Rectangle selection) =>
        selection.Width > 0 && selection.Height > 0 && MonitorBounds.Contains(session.Current)
            ? $"{selection.Width} × {selection.Height}" + (session.FixedSelectionValid ? "" : " 画面外") : null;

    private void DrawPointerHint(Graphics graphics, Rectangle selection)
    {
        if (session.PointerHint is not string hint || !MonitorBounds.Contains(session.Pointer)) return;
        DrawPointerLabel(graphics, hint, session.Pointer, HintOffset(selection), reserveHint: false);
    }

    private int HintOffset(Rectangle selection) => selection.Width > 0 && selection.Height > 0
        && MonitorBounds.Contains(session.Current) && Math.Abs(session.Current.Y - session.Pointer.Y) < 52 ? 52 : 24;

    private Rectangle PointerLabelBounds(string text, Point pointer, int offset, bool reserveHint)
    {
        string[] lines = text.Split('\n');
        int height = TextRenderer.MeasureText("国", dimensionsFont).Height * lines.Length + 4;
        int width = Math.Min(ClientSize.Width, lines.Max(line => TextRenderer.MeasureText(line, dimensionsFont).Width) + 8);
        int x = Math.Clamp(pointer.X - MonitorBounds.X + 18, 0, Math.Max(0, ClientSize.Width - width));
        int reserved = reserveHint && session.PointerHint is string hint ? (1 + hint.Count(c => c == '\n')) * height : 0;
        int y = Math.Clamp(pointer.Y - MonitorBounds.Y + offset, 0, Math.Max(0, ClientSize.Height - height - reserved));
        return new(x, y, width, height);
    }

    private void DrawPointerLabel(Graphics graphics, string text, Point pointer, int offset, bool reserveHint)
    {
        Rectangle label = PointerLabelBounds(text, pointer, offset, reserveHint);
        graphics.FillRectangle(Brushes.Black, label);
        string[] lines = text.Split('\n');
        int lineHeight = (label.Height - 4) / lines.Length;
        label.Offset(0, 2);
        label.Height = lineHeight;
        foreach (string line in lines)
        {
            TextRenderer.DrawText(graphics, line, dimensionsFont, label, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            label.Offset(0, lineHeight);
        }
    }

    private void DrawSnapshot(Graphics graphics, Rectangle dirty)
    {
        if (dirty.Width <= 0 || dirty.Height <= 0) return;
        Rectangle source = dirty;
        source.Offset(MonitorBounds.X - session.Snapshot.Bounds.X, MonitorBounds.Y - session.Snapshot.Bounds.Y);
        graphics.DrawImage(session.Snapshot.Image, dirty, source, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) dimensionsFont.Dispose();
        base.Dispose(disposing);
    }
}
