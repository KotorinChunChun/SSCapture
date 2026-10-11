namespace SSCapture.Capture;

/// <summary>Input-independent physical-pixel selection state shared by the overlays.</summary>
internal sealed class SelectionGesture
{
    private readonly CaptureMode mode;
    private readonly IReadOnlyList<Rectangle> monitors;
    private readonly IReadOnlyList<Rectangle> windows;
    private readonly SelectionSnap snapEdges;
    private readonly Size fixedDimensions;
    private readonly Size anchorOffset;
    private Point start;
    private Point rawStart;
    private Size threshold;
    private bool pointerInitialized;
    private bool previousSnap;
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
        this.windows = windows.ToArray();
        snapEdges = new SelectionSnap(this.monitors, this.windows);
        fixedDimensions = mode == CaptureMode.FixedRegion ? CaptureGeometry.ValidateFixedSize(fixedSize) : Size.Empty;
        anchorOffset = new Size(fixedAnchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? fixedDimensions.Width : 0,
            fixedAnchor is CaptureAnchor.BottomLeft or CaptureAnchor.BottomRight ? fixedDimensions.Height : 0);
    }

    internal void Begin(Point point, Size dragThreshold, bool snap = false)
    {
        rawStart = point;
        start = Resolve(point, snap);
        threshold = dragThreshold;
        IsPressed = true;
        HasDragged = false;
        Points.Clear();
        Move(point, snap);
    }

    internal void Move(Point point, bool snap = false)
    {
        bool unchanged = pointerInitialized && RawCurrent == point && previousSnap == snap;
        RawCurrent = point;
        previousSnap = snap;
        pointerInitialized = true;
        if (mode == CaptureMode.FixedRegion)
        {
            // Repeated pointer messages and the confirming click retain arrow nudges.
            // A physical move or Alt transition deliberately returns to the pointer.
            if (!unchanged) PositionFixed(Resolve(point, snap));
            return;
        }
        Current = Resolve(point, snap);
        if (mode == CaptureMode.Window)
        {
            Selection = WindowAt(point);
            return;
        }
        if (!IsPressed) return;
        HasDragged |= ExceedsThreshold(rawStart, point, threshold);
        if (Points.Count == 0 || Points[^1] != point) Points.Add(point);
        Selection = HasDragged
            ? mode == CaptureMode.Freeform ? CaptureGeometry.PolygonBounds(Points) : CaptureGeometry.FromDrag(start, Current)
            : Rectangle.Empty;
    }

    internal CaptureSelection? End(Point point, OutputDestination destination, bool snap = false)
    {
        if (!IsPressed) return null;
        Move(point, snap);
        IsPressed = false;
        if (mode == CaptureMode.FixedRegion) return FixedSelection(destination);
        if (mode == CaptureMode.Window) return FromBounds(WindowAt(point), destination);
        if (!HasDragged) return FromBounds(WindowAt(rawStart), destination);
        if (!CaptureGeometry.IsUsable(Selection)) return null;
        if (mode != CaptureMode.Freeform) return new CaptureSelection(Selection, null, destination);
        return CaptureGeometry.HasPolygonArea(Points)
            ? new CaptureSelection(Selection, Array.AsReadOnly(Points.ToArray()), destination)
            : null;
    }

    internal Rectangle WindowAt(Point point) => windows.FirstOrDefault(window => window.Contains(point));

    internal CaptureSelection? WindowSelection(Point point, OutputDestination destination) => FromBounds(WindowAt(point), destination);
    internal CaptureSelection? MonitorSelection(Point point, OutputDestination destination) => mode == CaptureMode.FixedRegion
        ? null : FromBounds(monitors.FirstOrDefault(monitor => monitor.Contains(point)), destination);

    internal CaptureSelection? FixedSelection(OutputDestination destination) =>
        mode == CaptureMode.FixedRegion && CaptureGeometry.IsFullyCovered(Selection, monitors)
            ? new CaptureSelection(Selection, null, destination) : null;

    internal bool Nudge(Keys keyData)
    {
        if (mode != CaptureMode.FixedRegion || (keyData & Keys.Modifiers & ~Keys.Shift) != 0) return false;
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
        if (mode != CaptureMode.FixedRegion) Selection = Rectangle.Empty;
        Points.Clear();
    }

    internal static bool ExceedsThreshold(Point start, Point point, Size threshold) =>
        Math.Abs((long)point.X - start.X) >= Math.Max(1, threshold.Width / 2) ||
        Math.Abs((long)point.Y - start.Y) >= Math.Max(1, threshold.Height / 2);

    private Point Resolve(Point point, bool snap) => snap && (mode is CaptureMode.Rectangle or CaptureMode.FixedRegion)
        ? snapEdges.Apply(point) : point;

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
