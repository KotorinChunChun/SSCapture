namespace SSCapture.Capture;

/// <summary>Input-independent physical-pixel selection state shared by the overlays.</summary>
internal sealed class SelectionGesture
{
    private readonly CaptureMode mode;
    private IReadOnlyList<Rectangle> monitors;
    private IReadOnlyList<Rectangle> windows;
    private Rectangle? desktopBounds;
    private SelectionSnap snapEdges;
    internal Rectangle? WindowLimit { get; set; }
    private readonly Size fixedDimensions;
    private Size anchorOffset;
    internal CaptureAnchor FixedAnchor { get; private set; }
    internal CaptureAspectRatio AspectRatio { get; set; }
    private bool restarted;
    private Point start;
    private Point rawStart;
    private Size threshold;
    private bool pointerInitialized;
    private bool previousSnap;
    private readonly DragAxis horizontal = new();
    private readonly DragAxis vertical = new();
    internal bool Grid20 { get; set; }
    internal Rectangle? FixedPosition { get; set; }
    internal int SnapDistancePixels { get; private set; } = 20;
    internal int GridStepPixels { get; private set; } = 20;
    internal void SetSnapPrecision(int distance, int step)
    {
        if (step is < 1 or > 32768) throw new ArgumentOutOfRangeException(nameof(step));
        var next = new SelectionSnap(monitors, windows, distance);
        SnapDistancePixels = distance; GridStepPixels = step; snapEdges = next;
    }
    internal bool IsPressed { get; private set; }
    internal bool HasDragged { get; private set; }
    internal Point Current { get; private set; }
    internal Point RawCurrent { get; private set; }
    internal Rectangle Selection { get; private set; }
    internal List<Point> Points { get; } = [];

    internal SelectionGesture(CaptureMode mode, IReadOnlyList<Rectangle> monitors, IReadOnlyList<Rectangle> windows,
        Size? fixedSize = null, CaptureAnchor fixedAnchor = CaptureAnchor.TopLeft)
    {
        if (!Enum.IsDefined(fixedAnchor)) throw new ArgumentOutOfRangeException(nameof(fixedAnchor));
        this.mode = mode;
        this.monitors = monitors.ToArray();
        desktopBounds = this.monitors.Count == 0 ? null : this.monitors.Aggregate(Rectangle.Union);
        this.windows = windows.ToArray();
        snapEdges = new SelectionSnap(this.monitors, this.windows);
        fixedDimensions = mode == CaptureMode.FixedRegion ? CaptureGeometry.ValidateFixedSize(fixedSize) : Size.Empty;
        FixedAnchor = fixedAnchor;
        anchorOffset = new Size(fixedAnchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? fixedDimensions.Width : 0,
            fixedAnchor is CaptureAnchor.BottomLeft or CaptureAnchor.BottomRight ? fixedDimensions.Height : 0);
    }

    internal void Begin(Point point, Size dragThreshold, bool snap = false, bool lockHeight = false, bool lockWidth = false)
    {
        rawStart = point;
        start = Resolve(point, snap);
        threshold = dragThreshold;
        IsPressed = true;
        HasDragged = false;
        restarted = false;
        horizontal.Reset();
        vertical.Reset();
        Points.Clear();
        Move(point, snap, lockHeight, lockWidth);
    }

    internal void Move(Point point, bool snap = false, bool lockHeight = false, bool lockWidth = false)
    {
        bool unchanged = pointerInitialized && RawCurrent == point && previousSnap == snap;
        RawCurrent = point;
        previousSnap = snap;
        pointerInitialized = true;
        if (mode == CaptureMode.FixedRegion)
        {
            if (FixedPosition is Rectangle fixedPosition) { Selection = fixedPosition; return; }
            // Repeated pointer messages and the confirming click retain arrow nudges.
            // A physical move or Alt transition deliberately returns to the pointer.
            if (!unchanged) PositionFixed(Resolve(point, snap));
            return;
        }
        Point previous = Current;
        Current = Resolve(point, snap);
        if (mode == CaptureMode.Window)
        {
            Selection = WindowAt(point);
            return;
        }
        if (!IsPressed) return;
        HasDragged |= ExceedsThreshold(rawStart, point, threshold);
        if (mode == CaptureMode.Rectangle)
        {
            int x = horizontal.Prepare(point.X, lockWidth, previous.X, previous.X != start.X, HasDragged);
            int y = vertical.Prepare(point.Y, lockHeight, previous.Y, previous.Y != start.Y, HasDragged);
            if (desktopBounds is Rectangle desktop)
            {
                x = Math.Clamp(x, desktop.Left, desktop.Right);
                y = Math.Clamp(y, desktop.Top, desktop.Bottom);
            }
            Point adjusted = new(x, y);
            Point quantized = Grid20 || snap ? new(Quantize(x, start.X, true), Quantize(y, start.Y, false)) : adjusted;
            var snapped = Grid20 || snap ? snapEdges.Evaluate(adjusted, includeWindows: snap) : new SelectionSnap.SnapResult(adjusted, false, false);
            Current = new Point(horizontal.IsLocked ? horizontal.Value : snapped.X ? snapped.Point.X : quantized.X,
                vertical.IsLocked ? vertical.Value : snapped.Y ? snapped.Point.Y : quantized.Y);
            Current = AspectRatios.Constrain(start, Current, AspectRatio, desktopBounds, horizontal.IsLocked, vertical.IsLocked);
        }
        if (Points.Count == 0 || Points[^1] != point) Points.Add(point);
        Selection = HasDragged
            ? mode == CaptureMode.Freeform ? CaptureGeometry.PolygonBounds(Points) : CaptureGeometry.FromDrag(start, Current)
            : Rectangle.Empty;
    }

    internal CaptureSelection? End(Point point, OutputDestination destination, bool snap = false, bool lockHeight = false, bool lockWidth = false)
    {
        if (!IsPressed) return null;
        Move(point, snap, lockHeight, lockWidth);
        IsPressed = false;
        if (mode == CaptureMode.FixedRegion) return FixedSelection(destination);
        if (mode == CaptureMode.Window) return FromBounds(WindowAt(point), destination);
        if (!HasDragged) return restarted ? null : FromBounds(WindowAt(rawStart), destination);
        if (!CaptureGeometry.IsUsable(Selection)) return null;
        if (mode != CaptureMode.Freeform) return new CaptureSelection(Selection, null, destination);
        return CaptureGeometry.HasPolygonArea(Points)
            ? new CaptureSelection(Selection, Array.AsReadOnly(Points.ToArray()), destination)
            : null;
    }

    internal Rectangle WindowAt(Point point)
    {
        Rectangle found = windows.FirstOrDefault(window => window.Contains(point));
        return WindowLimit is Rectangle limit ? Rectangle.Intersect(found, limit) : found;
    }

    internal void ExpandMonitors(IReadOnlyList<Rectangle> expanded)
    {
        monitors = expanded.ToArray();
        WindowLimit = null;
        desktopBounds = monitors.Aggregate(Rectangle.Union);
        snapEdges = new SelectionSnap(monitors, windows, SnapDistancePixels);
    }

    internal void PlaceFixed(Rectangle bounds, Point pointer)
    {
        if (mode != CaptureMode.FixedRegion || bounds.Size != fixedDimensions) throw new ArgumentException("固定枠の寸法が一致しません。");
        Move(pointer);
        anchorOffset = new Size(pointer.X - bounds.X, pointer.Y - bounds.Y);
        PositionFixed(pointer);
    }

    internal void UpdateTargets(IReadOnlyList<Rectangle> monitors, IReadOnlyList<Rectangle> windows, Rectangle? limit)
    {
        this.windows = windows.ToArray(); ExpandMonitors(monitors); WindowLimit = limit;
    }
    internal void ChangeAnchor(CaptureAnchor anchor)
    {
        if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
        FixedAnchor = anchor;
        anchorOffset = new Size(anchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? fixedDimensions.Width : 0,
            anchor is CaptureAnchor.BottomLeft or CaptureAnchor.BottomRight ? fixedDimensions.Height : 0);
        if (mode == CaptureMode.FixedRegion && FixedPosition is null) PositionFixed(RawCurrent);
    }

    internal bool Space()
    {
        if (FixedPosition is not null) return true;
        if (mode == CaptureMode.FixedRegion)
        {
            FixedAnchor = (CaptureAnchor)(((int)FixedAnchor + 1) % 4);
            anchorOffset = new Size(FixedAnchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? fixedDimensions.Width : 0,
                FixedAnchor is CaptureAnchor.BottomLeft or CaptureAnchor.BottomRight ? fixedDimensions.Height : 0);
            PositionFixed(RawCurrent);
            return true;
        }
        if (mode != CaptureMode.Rectangle || !IsPressed) return false;
        Begin(RawCurrent, threshold);
        restarted = true;
        return true;
    }

    internal CaptureSelection? WindowSelection(Point point, OutputDestination destination) => FromBounds(WindowAt(point), destination);
    internal CaptureSelection? MonitorSelection(Point point, OutputDestination destination) =>
        FromBounds(monitors.FirstOrDefault(monitor => monitor.Contains(point)), destination);

    internal CaptureSelection? FixedSelection(OutputDestination destination) =>
        mode == CaptureMode.FixedRegion && CaptureGeometry.IsFullyCovered(Selection, monitors)
            ? new CaptureSelection(Selection, null, destination) : null;

    internal bool Nudge(Keys keyData)
    {
        if (mode != CaptureMode.FixedRegion || FixedPosition is not null || (keyData & Keys.Modifiers & ~Keys.Shift) != 0) return false;
        int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
        Size movement = (keyData & Keys.KeyCode) switch
        {
            Keys.Left => new Size(-step, 0), Keys.Right => new Size(step, 0),
            Keys.Up => new Size(0, -step), Keys.Down => new Size(0, step), _ => Size.Empty
        };
        if (movement.IsEmpty) return false;
        PositionFixed((long)Current.X + movement.Width, (long)Current.Y + movement.Height);
        return true;
    }

    internal void Reset()
    {
        IsPressed = false;
        HasDragged = false;
        horizontal.Reset();
        vertical.Reset();
        if (mode != CaptureMode.FixedRegion) Selection = Rectangle.Empty;
        Points.Clear();
    }

    internal static bool ExceedsThreshold(Point start, Point point, Size threshold) =>
        Math.Abs((long)point.X - start.X) >= Math.Max(1, threshold.Width / 2) ||
        Math.Abs((long)point.Y - start.Y) >= Math.Max(1, threshold.Height / 2);

    private Point Resolve(Point point, bool snap) => snap && (mode is CaptureMode.Rectangle or CaptureMode.FixedRegion)
        ? snapEdges.Apply(point) : point;

    private int Quantize(int coordinate, int origin, bool horizontalAxis)
    {
        long delta = (long)coordinate - origin;
        long step = GridStepPixels;
        long units = (Math.Abs(delta) + step / 2) / step;
        long result = origin + Math.Sign(delta) * units * step;
        if (desktopBounds is Rectangle monitor)
        {
            int first = horizontalAxis ? monitor.Left : monitor.Top;
            int last = horizontalAxis ? monitor.Right : monitor.Bottom;
            if (result < first) result = origin - ((long)origin - first) / step * step;
            if (result > last) result = origin + ((long)last - origin) / step * step;
        }
        return (int)Math.Clamp(result, int.MinValue, int.MaxValue);
    }

    private sealed class DragAxis
    {
        internal bool IsLocked { get; private set; }
        internal int Value { get; private set; }

        internal int Prepare(int raw, bool pressed, int previous, bool hasDimension, bool dragged)
        {
            if (IsLocked && !pressed) IsLocked = false;
            if (!IsLocked && pressed && dragged)
            {
                Value = hasDimension ? previous : raw;
                IsLocked = true;
            }
            return raw;
        }

        internal void Reset() => IsLocked = false;
    }

    private void PositionFixed(Point point) => PositionFixed(point.X, point.Y);

    private void PositionFixed(long x, long y)
    {
        Current = new Point((int)Math.Clamp(x, (long)int.MinValue + anchorOffset.Width, (long)int.MaxValue - fixedDimensions.Width + anchorOffset.Width),
            (int)Math.Clamp(y, (long)int.MinValue + anchorOffset.Height, (long)int.MaxValue - fixedDimensions.Height + anchorOffset.Height));
        Selection = new Rectangle(Current.X - anchorOffset.Width, Current.Y - anchorOffset.Height, fixedDimensions.Width, fixedDimensions.Height);
    }

    private static CaptureSelection? FromBounds(Rectangle bounds, OutputDestination destination) =>
        CaptureGeometry.IsUsable(bounds) ? new CaptureSelection(bounds, null, destination) : null;
}
