using System.Drawing.Drawing2D;

namespace SSCapture.Capture;

internal sealed class CaptureOverlay : Form
{
    private readonly SessionController session;
    private readonly Font dimensionsFont = new("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private bool sessionNotifications;
    private CapturePresentation? sharedPresentation;
    private VisualState? rendered;
    private Keys? mouseButtonModifiers;
    private int magnifierQuadrant = -1;
    internal Rectangle MonitorBounds { get; }
    internal CaptureContextMenu SelectionMenu => session.SelectionMenu;

    private sealed record VisualState(DesktopSnapshot Snapshot, CaptureMode Mode, bool Dim, bool Highlight,
        Rectangle Selection, Rectangle Target, WindowCaptureFrame? Preview, bool Valid, int Points, Rectangle Dimensions, string? DimensionsText,
        Rectangle Hint, string? HintText, string? Error, int TrailPoints, Rectangle TrailBounds, IReadOnlyList<Rectangle> DragHighlights,
        Rectangle Magnifier, Point MagnifierSample, Rectangle MagnifierSelection, bool MagnifierMarksOrigin,
        int MagnifierDotCount, int MagnifierZoom, bool Shared);

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
        sharedPresentation?.Dispose();
        sharedPresentation = null;
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

    internal void RaiseWithoutActivation() => CaptureNative.RaiseWithoutActivation(Handle);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021 && session.SelectingControl) { m.Result = 3; return; } // MA_NOACTIVATE
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
        if (m.Msg is >= 0x0201 and <= 0x0209) // WM_*BUTTONDOWN / UP / DBLCLK
        {
            int state = m.WParam.ToInt32();
            Keys? previous = mouseButtonModifiers;
            mouseButtonModifiers = ((state & 0x0008) != 0 ? Keys.Control : Keys.None)
                | ((state & 0x0004) != 0 ? Keys.Shift : Keys.None);
            try
            {
                var button = m.Msg <= 0x0203 ? MouseButtons.Left : m.Msg <= 0x0206 ? MouseButtons.Right : MouseButtons.Middle;
                // WinForms releases capture on a second button's mouse-up.
                if (session.IsSecondaryPress(button))
                {
                    long packed = m.LParam.ToInt64();
                    var point = new Point(MonitorBounds.X + unchecked((short)packed),
                        MonitorBounds.Y + unchecked((short)(packed >> 16)));
                    if (m.Msg is 0x0202 or 0x0205 or 0x0208) session.MouseUp(button, point, mouseButtonModifiers);
                    else session.MouseDown(this, button, point, mouseButtonModifiers);
                }
                else base.WndProc(ref m);
            }
            finally { mouseButtonModifiers = previous; }
        }
        else base.WndProc(ref m);
        // WinForms can apply WM_DPICHANGED's suggested (scaled) bounds even with AutoScale=None.
        // Reassert this monitor's physical bounds after its internal DPI bookkeeping.
        if (m.Msg == CaptureNative.WmDpiChanged) FixPhysicalBounds();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (session.HandleCommandKey(keyData, (msg.LParam.ToInt64() & (1L << 30)) != 0)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); session.MouseDown(this, e.Button, new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y), mouseButtonModifiers); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); session.Move(true, new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y)); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); session.MouseUp(e.Button, new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y), mouseButtonModifiers); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        session.MouseWheel(new Point(MonitorBounds.X + e.X, MonitorBounds.Y + e.Y), e.Delta);
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); session.CaptureLost(this); }
    protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); session.Cancel(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle dirty = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
        DrawSnapshot(e.Graphics, dirty);
        VisualState visual = ReadVisualState();
        if (visual.Shared && Visible && !session.Refreshing)
        {
            sharedPresentation ??= new CapturePresentation(MonitorBounds, Handle);
            if (sharedPresentation.Present(dirty, (graphics, area, shared) => DrawForeground(graphics, area, shared, visual)))
            {
                rendered ??= visual;
                return;
            }
        }
        sharedPresentation?.Hide();
        DrawForeground(e.Graphics, dirty, shared: false, visual);
        if (rendered is null || dirty.Contains(ClientRectangle)) rendered = visual;
    }

    private void DrawForeground(Graphics graphics, Rectangle dirty, bool shared, VisualState visual)
    {
        Rectangle target = visual.Target;
        Rectangle selection = Local(visual.Selection);
        if (session.DimCaptureBackground)
        {
            using SolidBrush dim = new(Color.FromArgb(115, Color.Black));
            if (shared)
            {
                using Region mask = new(dirty);
                mask.Exclude(Local(target));
                if (!session.SelectingControl && session.Mode == CaptureMode.Freeform && session.Points.Count >= 3)
                {
                    using var path = FreeformPath();
                    mask.Exclude(path);
                }
                else if (!session.HighlightWindow) mask.Exclude(selection);
                foreach (Rectangle window in session.TargetDragHighlights) mask.Exclude(Local(window));
                graphics.FillRegion(dim, mask);
            }
            else graphics.FillRectangle(dim, dirty);
        }
        Rectangle bright = Rectangle.Intersect(Local(target), dirty);
        if (visual.Preview is { } frame && bright.Width > 0 && bright.Height > 0
            && frame.Bounds.Contains(target))
        {
            if (session.DimCaptureBackground && !shared) graphics.FillRectangle(Brushes.Black, bright);
            Rectangle source = bright;
            source.Offset(MonitorBounds.X - frame.Bounds.X, MonitorBounds.Y - frame.Bounds.Y);
            graphics.DrawImage(frame.Image, bright, source, GraphicsUnit.Pixel);
        }
        else if (session.DimCaptureBackground && !shared && !session.UsesControlPreview) DrawSnapshot(graphics, bright);
        if (!shared)
            foreach (Rectangle window in session.TargetDragHighlights)
                DrawSnapshot(graphics, Rectangle.Intersect(Local(window), dirty));
        DrawSelection(graphics, selection, dirty, shared);
        DrawTargetTrail(graphics, dirty);
        if (visual.DimensionsText is string dimensions)
            DrawPointerLabel(graphics, dimensions, visual.Dimensions, shared);
        if (visual.HintText is string hint)
            DrawPointerLabel(graphics, hint, visual.Hint, shared);
        DrawMagnifier(graphics, visual);
        if (session.SelectionError is string error)
        {
            var box = new Rectangle(16, 16, Math.Max(1, ClientSize.Width - 32), 100);
            graphics.FillRectangle(Brushes.White, box);
            graphics.DrawString(error, dimensionsFont, Brushes.DarkRed, box);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) sharedPresentation?.Hide();
        else Invalidate();
    }

    internal bool InvalidateChanges()
    {
        var next = ReadVisualState();
        if (rendered is not { } old || old.Snapshot != next.Snapshot || old.Mode != next.Mode
            || old.Dim != next.Dim || old.Highlight != next.Highlight || old.Error != next.Error || old.Shared != next.Shared)
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
            var path = session.TargetDragPath;
            for (int index = Math.Max(1, old.TrailPoints); index < path.Count; index++)
                dirty.Union(Inflate(SegmentBounds(path[index - 1], path[index])));
        }
        if (old.Dimensions != next.Dimensions || old.DimensionsText != next.DimensionsText)
        {
            dirty.Union(old.Dimensions);
            dirty.Union(next.Dimensions);
        }
        if (old.Hint != next.Hint || old.HintText != next.HintText)
        {
            dirty.Union(old.Hint);
            dirty.Union(next.Hint);
        }
        if (old.Magnifier != next.Magnifier || old.MagnifierSample != next.MagnifierSample
            || old.MagnifierSelection != next.MagnifierSelection || old.MagnifierMarksOrigin != next.MagnifierMarksOrigin
            || old.MagnifierDotCount != next.MagnifierDotCount || old.MagnifierZoom != next.MagnifierZoom
            || old.Preview != next.Preview || old.Points != next.Points)
        {
            dirty.Union(old.Magnifier);
            dirty.Union(next.Magnifier);
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
        var (dimensions, dimensionsPointer, hint) = ReadPointerLabels(selection);
        Rectangle dimensionsBounds = dimensions is null ? Rectangle.Empty : PointerLabelBounds(dimensions, dimensionsPointer, 24, hint);
        Rectangle hintBounds = hint is null ? Rectangle.Empty : PointerLabelBounds(hint, session.Pointer, HintOffset(dimensions, dimensionsPointer));
        int dotCount = session.OutputSettings.MagnifierDotCount, zoom = session.OutputSettings.MagnifierZoom;
        CaptureMagnifier.Placement lens = session.OutputSettings.ShowMagnifier && MonitorBounds.Contains(session.Pointer)
            ? CaptureMagnifier.Place(ClientRectangle, new Point(session.Pointer.X - MonitorBounds.X, session.Pointer.Y - MonitorBounds.Y),
                [dimensionsBounds, hintBounds, Local(session.VisibleGuideBounds)], magnifierQuadrant, dotCount * zoom)
            : new(Rectangle.Empty);
        magnifierQuadrant = lens.Quadrant;
        return new(session.Snapshot, session.Mode, session.DimCaptureBackground, session.HighlightWindow, selection,
            session.HighlightForOverlay(MonitorBounds), session.PreviewFrame, session.FixedSelectionValid, session.Points.Count,
            dimensionsBounds, dimensions, hintBounds, hint, session.SelectionError, session.TargetDragPath.Count, session.TargetDragBounds,
            session.TargetDragHighlights, lens.Bounds, lens.Bounds.IsEmpty ? Point.Empty : session.MagnifierSample,
            lens.Bounds.IsEmpty ? Rectangle.Empty : session.MagnifierSelection,
            !lens.Bounds.IsEmpty && session.MagnifierMarksOrigin, dotCount, zoom, session.OutputSettings.ShareCapturePresentation);
    }

    private static Rectangle SegmentBounds(Point start, Point end) => Rectangle.FromLTRB(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X) + 1, Math.Max(start.Y, end.Y) + 1);

    private void DrawTargetTrail(Graphics graphics, Rectangle dirty)
    {
        var path = session.TargetDragPath;
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

    private void DrawSelection(Graphics graphics, Rectangle selection, Rectangle dirty, bool shared)
    {
        using Pen border = new(session.FixedSelectionValid ? Color.FromArgb(55, 195, 255) : Color.OrangeRed, 2);
        if (!session.SelectingControl && session.Mode == CaptureMode.Freeform && session.Dragging
            && session.Points.Count >= 2 && (selection.IsEmpty || session.MagnifierPolygon is not null))
            DrawFreeformSelection(graphics, border, dirty, shared);
        else if (selection.Width > 0 && selection.Height > 0)
        {
            if (!session.HighlightWindow && !session.SelectingControl && !shared) DrawSnapshot(graphics, Rectangle.Intersect(selection, dirty));
            graphics.DrawRectangle(border, selection);
        }
    }

    private GraphicsPath FreeformPath()
    {
        var path = new GraphicsPath(FillMode.Alternate);
        path.AddPolygon(session.Points.Select(p => new Point(p.X - MonitorBounds.X, p.Y - MonitorBounds.Y)).ToArray());
        return path;
    }

    private void DrawFreeformSelection(Graphics graphics, Pen border, Rectangle dirty, bool shared)
    {
        Point[] points = session.Points.Select(p => new Point(p.X - MonitorBounds.X, p.Y - MonitorBounds.Y)).ToArray();
        if (points.Length >= 3)
        {
            using GraphicsPath path = new(FillMode.Alternate);
            path.AddPolygon(points);
            if (!session.HighlightWindow && !shared)
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

    private (string? Dimensions, Point DimensionsPointer, string? Hint) ReadPointerLabels(Rectangle selection)
    {
        Point pointer = session.SelectingControl ? session.Pointer : session.Current;
        string? dimensions = null;
        if (!session.PointerLabelsSuppressed && selection.Width > 0 && selection.Height > 0 && MonitorBounds.Contains(pointer)
            && selection != session.PointerHintCoordinates)
            dimensions = $"{selection.Width} × {selection.Height}" + (session.FixedSelectionValid ? "" : " 画面外");
        return (dimensions, pointer, PointerHintText());
    }

    private int HintOffset(string? dimensions, Point dimensionsPointer) =>
        dimensions is not null && Math.Abs(dimensionsPointer.Y - session.Pointer.Y) < 52
        ? 24 + Math.Max(28, PointerLabelLineHeight + 8) : 24;

    private int PointerLabelLineHeight => TextRenderer.MeasureText("国", dimensionsFont).Height;

    private string? PointerHintText()
    {
        if (!MonitorBounds.Contains(session.Pointer) || session.PointerHint is not string hint) return null;
        int lineHeight = PointerLabelLineHeight;
        int maximumLines = Math.Max(2, (ClientSize.Height - lineHeight - 16) / lineHeight);
        if (1 + hint.Count(c => c == '\n') <= maximumLines) return hint;
        string[] lines = hint.Split('\n');
        bool hasMargin = lines[^1].Contains("）　余白", StringComparison.Ordinal);
        var visible = lines.Take(maximumLines - (hasMargin ? 2 : 1)).Append($"ほか {lines.Length - maximumLines + 1} 件");
        return string.Join('\n', hasMargin ? visible.Append(lines[^1]) : visible);
    }

    private Rectangle PointerLabelBounds(string text, Point pointer, int offset, string? hintBelow = null)
    {
        string[] lines = text.Split('\n');
        int lineHeight = PointerLabelLineHeight;
        int height = lineHeight * lines.Length + 4;
        int width = Math.Min(ClientSize.Width, lines.Max(line => TextRenderer.MeasureText(line, dimensionsFont).Width) + 8);
        int x = Math.Clamp(pointer.X - MonitorBounds.X + 18, 0, Math.Max(0, ClientSize.Width - width));
        int reserved = hintBelow is null ? 0 : (1 + hintBelow.Count(c => c == '\n')) * lineHeight + 12;
        int y = Math.Clamp(pointer.Y - MonitorBounds.Y + offset, 0, Math.Max(0, ClientSize.Height - height - reserved));
        return new(x, y, width, height);
    }

    private void DrawPointerLabel(Graphics graphics, string text, Rectangle label, bool shared)
    {
        graphics.FillRectangle(Brushes.Black, label);
        string[] lines = text.Split('\n');
        int lineHeight = (label.Height - 4) / lines.Length;
        label.Offset(0, 2);
        label.Height = lineHeight;
        using var format = shared ? new StringFormat
        {
            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
        } : null;
        foreach (string line in lines)
        {
            if (format is not null)
                graphics.DrawString(line, dimensionsFont, Brushes.White, label, format);
            else TextRenderer.DrawText(graphics, line, dimensionsFont, label, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            label.Offset(0, lineHeight);
        }
    }

    private void DrawMagnifier(Graphics graphics, VisualState visual)
    {
        if (visual.Magnifier.IsEmpty) return;
        WindowCaptureFrame? preview = visual.Preview;
        var (image, source) = preview is not null
            ? (preview.Image, preview.Bounds) : (visual.Snapshot.Image, visual.Snapshot.Bounds);
        CaptureMagnifier.Draw(graphics, visual.Magnifier, image, source, visual.MagnifierSample,
            visual.MagnifierSelection, session.MagnifierPolygon, visual.MagnifierMarksOrigin, visual.MagnifierDotCount, visual.MagnifierZoom);
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
