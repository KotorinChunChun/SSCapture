using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SSCapture.Services;

internal enum PinnedDrawingTool { Freehand, Line, Rectangle, Ellipse, Arrow, Text }
internal enum PinnedGesture { Move, Select, Draw, AxisLine }

// 原画像と不変の注釈を分離する。履歴に巨大な画像の複製を積まない。
internal sealed class PinnedImageDocument : IDisposable
{
    internal sealed record Annotation(PinnedDrawingTool Tool, PointF[] Points, Color Color, float Width,
        int Orientation, string? Text = null);
    private sealed record State(int Orientation, Annotation[] Annotations);
    private readonly Bitmap original;
    private State current = new(0, []);
    private readonly List<State> undo = [], redo = [];
    private bool disposed;
    internal event EventHandler? Changed;
    internal Size Size => SizeAt(current.Orientation);
    internal int Orientation => current.Orientation;
    internal int AnnotationCount => current.Annotations.Length;
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal Rectangle? Selection { get; private set; }
    internal Color PenColor { get; set; } = Color.Red;
    internal float PenWidth { get; set; } = 3;
    internal bool Highlighter { get; set; }
    internal PinnedDrawingTool Tool { get; set; }

    internal PinnedImageDocument(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Capture.CaptureGeometry.ValidateFixedSize(image.Size);
        original = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(original);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(image, 0, 0);
    }

    internal static PinnedGesture Gesture(Keys modifiers) => (modifiers & (Keys.Control | Keys.Shift)) switch
    {
        Keys.Control | Keys.Shift => PinnedGesture.AxisLine,
        Keys.Control => PinnedGesture.Draw,
        Keys.Shift => PinnedGesture.Select,
        _ => PinnedGesture.Move
    };
    internal static PointF AxisEnd(PointF start, PointF end) => Math.Abs(end.X - start.X) >= Math.Abs(end.Y - start.Y)
        ? new(end.X, start.Y) : new(start.X, end.Y);
    internal PointF Clamp(PointF p) => new(Math.Clamp(p.X, 0, Size.Width), Math.Clamp(p.Y, 0, Size.Height));
    internal static PointF ImagePoint(PointF client, float zoom) => zoom > 0 && float.IsFinite(zoom)
        ? new(client.X / zoom, client.Y / zoom) : throw new ArgumentOutOfRangeException(nameof(zoom));

    internal Annotation MakeAnnotation(PinnedDrawingTool tool, IEnumerable<PointF> points, string? text = null)
    {
        var all = points.Select(Clamp).ToArray();
        if (all.Length == 0 || all.Length > 1_000_000) throw new ArgumentException("描画点の数が正しくありません。");
        if (tool == PinnedDrawingTool.Text && (string.IsNullOrWhiteSpace(text) || text.Length > 4096))
            throw new ArgumentException("文字は1～4096文字で指定してください。");
        if (!Enum.IsDefined(tool)) throw new ArgumentOutOfRangeException(nameof(tool));
        return new(tool, all, Color.FromArgb(Highlighter ? 80 : 255, PenColor), Math.Clamp(PenWidth, 1, 128), current.Orientation, text);
    }
    internal void Add(PinnedDrawingTool tool, IEnumerable<PointF> points, string? text = null)
    {
        ThrowIfDisposed();
        var note = MakeAnnotation(tool, points, text);
        Commit(current with { Annotations = [.. current.Annotations, note] });
    }
    internal void Clear()
    {
        ThrowIfDisposed();
        if (current.Annotations.Length > 0) Commit(current with { Annotations = [] });
    }
    internal void Rotate(bool clockwise)
    {
        ThrowIfDisposed();
        Selection = null;
        Commit(current with { Orientation = (current.Orientation + (clockwise ? 1 : 3)) % 4 });
    }
    internal void Undo()
    {
        ThrowIfDisposed();
        if (undo.Count == 0) return;
        redo.Add(current); current = undo[^1]; undo.RemoveAt(undo.Count - 1); Selection = null; RaiseChanged();
    }
    internal void Redo()
    {
        ThrowIfDisposed();
        if (redo.Count == 0) return;
        undo.Add(current); current = redo[^1]; redo.RemoveAt(redo.Count - 1); Selection = null; RaiseChanged();
    }
    internal void Select(PointF first, PointF second)
    {
        first = Clamp(first); second = Clamp(second);
        int left = (int)Math.Floor(Math.Min(first.X, second.X)), top = (int)Math.Floor(Math.Min(first.Y, second.Y));
        int right = Math.Min(Size.Width, (int)Math.Ceiling(Math.Max(first.X, second.X)));
        int bottom = Math.Min(Size.Height, (int)Math.Ceiling(Math.Max(first.Y, second.Y)));
        Selection = right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : null;
    }
    internal void ClearSelection() => Selection = null;
    internal Bitmap Render(bool selectionOnly = false)
    {
        ThrowIfDisposed();
        var result = (Bitmap)original.Clone();
        result.RotateFlip(current.Orientation switch { 1 => RotateFlipType.Rotate90FlipNone, 2 => RotateFlipType.Rotate180FlipNone,
            3 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        try
        {
            using (var g = Graphics.FromImage(result))
                foreach (var note in current.Annotations)
                {
                    var state = g.Save();
                    using var transform = RotationTransform((current.Orientation - note.Orientation + 4) % 4, SizeAt(note.Orientation));
                    g.Transform = transform;
                    PaintAnnotation(g, note);
                    g.Restore(state);
                }
            if (selectionOnly && Selection is Rectangle selection)
            {
                var cropped = result.Clone(selection, PixelFormat.Format32bppArgb);
                result.Dispose(); return cropped;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal static void PaintAnnotation(Graphics g, Annotation note)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(note.Color, note.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF start = note.Points[0], end = note.Points[^1];
        var bounds = new RectangleF(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(0.1f, Math.Abs(end.X - start.X)), Math.Max(0.1f, Math.Abs(end.Y - start.Y)));
        switch (note.Tool)
        {
            case PinnedDrawingTool.Freehand:
                if (note.Points.Length > 1) g.DrawLines(pen, note.Points);
                else { using var dot = new SolidBrush(note.Color); g.FillEllipse(dot, start.X - note.Width / 2, start.Y - note.Width / 2, note.Width, note.Width); }
                break;
            case PinnedDrawingTool.Line: g.DrawLine(pen, start, end); break;
            case PinnedDrawingTool.Rectangle: g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height); break;
            case PinnedDrawingTool.Ellipse: g.DrawEllipse(pen, bounds); break;
            case PinnedDrawingTool.Arrow:
                using (var cap = new AdjustableArrowCap(4, 5, true)) { pen.CustomEndCap = cap; g.DrawLine(pen, start, end); }
                break;
            case PinnedDrawingTool.Text:
                using (var font = new Font("Yu Gothic UI", Math.Max(12, note.Width * 6), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(note.Color)) g.DrawString(note.Text, font, brush, start, StringFormat.GenericTypographic);
                break;
        }
    }
    private Size SizeAt(int orientation) => orientation % 2 == 0 ? original.Size : new(original.Height, original.Width);
    private static Matrix RotationTransform(int turns, Size source) => turns switch
    {
        1 => new(0, 1, -1, 0, source.Height, 0),
        2 => new(-1, 0, 0, -1, source.Width, source.Height),
        3 => new(0, -1, 1, 0, 0, source.Width),
        _ => new()
    };
    private void Commit(State next)
    {
        undo.Add(current); if (undo.Count > 256) undo.RemoveAt(0);
        current = next; redo.Clear(); RaiseChanged();
    }
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { if (disposed) return; disposed = true; original.Dispose(); undo.Clear(); redo.Clear(); }
}
