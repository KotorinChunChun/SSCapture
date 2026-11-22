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
    private Point? swappedPointer;
    private Rectangle rectangleBase;
    private Size rectangleWheelOffset;
    private int rectangleWheelRemainder;
    private (Rectangle Bounds, Point Pointer)? centerMove;
    private Point start;
    private Point rawStart;
    private Size threshold;
    private bool pointerInitialized;
    private bool previousSnap;
    private CaptureKeyAction rectangleAction;
    private double? dragAspect;
    private readonly DragAxis horizontal = new();
    private readonly DragAxis vertical = new();
    internal bool Grid20 { get; set; }
    internal Rectangle? FixedPosition { get; set; }
    internal int SnapDistancePixels { get; private set; } = 20;
    internal int GridStepPixels { get; private set; } = Services.AppSettings.DefaultGridStepPixels;
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
    internal Point MagnifierPoint => mode == CaptureMode.Rectangle && IsPressed && !HasDragged ? start
        : mode == CaptureMode.Rectangle && Selection != rectangleBase && centerMove is null ? AnchorPoint(Selection, PointerCorner(rectangleBase, Current)) : Current;
    internal Point WindowTargetPoint => IsPressed && !HasDragged ? rawStart : RawCurrent;
    internal Rectangle Selection { get; private set; }
    internal Rectangle PressedWindow => IsPressed && !HasDragged && mode is (CaptureMode.Rectangle or CaptureMode.Freeform)
        ? WindowAt(rawStart) : Rectangle.Empty;
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
        anchorOffset = OffsetFor(fixedAnchor);
    }

    internal void Begin(Point point, Size dragThreshold, bool snap = false, bool lockHeight = false, bool lockWidth = false,
        CaptureKeyAction action = CaptureKeyAction.None)
    {
        rawStart = point;
        start = Resolve(point, snap);
        threshold = dragThreshold;
        IsPressed = true;
        HasDragged = false;
        swappedPointer = null;
        ResetRectangleAdjustments();
        dragAspect = null;
        if (mode == CaptureMode.Rectangle) Selection = Rectangle.Empty;
        horizontal.Reset();
        vertical.Reset();
        Points.Clear();
        Move(point, snap, lockHeight, lockWidth, action);
    }

    internal void Move(Point point, bool snap = false, bool lockHeight = false, bool lockWidth = false,
        CaptureKeyAction action = CaptureKeyAction.None, bool precise = false)
    {
        bool unchanged = pointerInitialized && RawCurrent == point && previousSnap == snap;
        RawCurrent = point;
        previousSnap = snap;
        pointerInitialized = true;
        if (swappedPointer == point && rectangleAction == action) return;
        swappedPointer = null;
        rectangleAction = action;
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
            if (IsPressed) HasDragged |= ExceedsThreshold(rawStart, point, threshold);
            Selection = WindowAt(point);
            return;
        }
        if (!IsPressed) return;
        HasDragged |= ExceedsThreshold(rawStart, point, threshold);
        if (centerMove is { } sliding)
        {
            Rectangle limit = desktopBounds ?? sliding.Bounds;
            int x = (int)Math.Clamp((long)sliding.Bounds.X + point.X - sliding.Pointer.X, limit.Left, limit.Right - sliding.Bounds.Width);
            int y = (int)Math.Clamp((long)sliding.Bounds.Y + point.Y - sliding.Pointer.Y, limit.Top, limit.Bottom - sliding.Bounds.Height);
            rectangleBase = new(new Point(x, y), sliding.Bounds.Size);
            Selection = AdjustedRectangle();
            Current = AnchorPoint(Selection, CaptureAnchor.Center);
            return;
        }
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
            bool grid = !precise && (Grid20 || action == CaptureKeyAction.Grid);
            Point quantized = grid ? new(Quantize(x, start.X, true), Quantize(y, start.Y, false)) : adjusted;
            var snapped = grid || snap ? snapEdges.Evaluate(adjusted, includeWindows: snap) : new SelectionSnap.SnapResult(adjusted, false, false);
            Current = new Point(horizontal.IsLocked ? horizontal.Value : snapped.X ? snapped.Point.X : quantized.X,
                vertical.IsLocked ? vertical.Value : snapped.Y ? snapped.Point.Y : quantized.Y);
            bool lockAspect = action is CaptureKeyAction.LockAspectRatio or CaptureKeyAction.FromCenterWithAspectRatio;
            if (lockAspect)
                dragAspect ??= Selection.Width > 0 && Selection.Height > 0 ? (double)Selection.Width / Selection.Height : 1;
            else dragAspect = null;
            Rectangle? limits = IsCentered ? CenteredLimits() : desktopBounds;
            if (limits is Rectangle boundary)
                Current = new(Math.Clamp(Current.X, boundary.Left, boundary.Right), Math.Clamp(Current.Y, boundary.Top, boundary.Bottom));
            Current = dragAspect is double ratio && AspectRatio == CaptureAspectRatio.Free
                ? AspectRatios.ConstrainDrag(start, Current, ratio, limits)
                : AspectRatios.Constrain(start, Current, AspectRatio, limits, horizontal.IsLocked, vertical.IsLocked);
        }
        if (Points.Count == 0 || Points[^1] != point) Points.Add(point);
        Selection = HasDragged
            ? mode == CaptureMode.Freeform ? CaptureGeometry.PolygonBounds(Points)
                : CaptureGeometry.FromDrag(IsCentered ? Reflect(Current) : start, Current)
            : Rectangle.Empty;
        if (mode == CaptureMode.Rectangle)
        {
            rectangleBase = Selection;
            Selection = AdjustedRectangle();
        }
    }

    internal CaptureSelection? End(Point point, OutputDestination destination, bool snap = false, bool lockHeight = false, bool lockWidth = false,
        CaptureKeyAction action = CaptureKeyAction.None, bool precise = false)
    {
        if (!IsPressed) return null;
        Move(point, snap, lockHeight, lockWidth, action, precise);
        IsPressed = false;
        if (mode == CaptureMode.FixedRegion) return FixedSelection(destination);
        if (mode == CaptureMode.Window) return WindowSelection(HasDragged ? point : rawStart, destination);
        if (!HasDragged) return WindowSelection(rawStart, destination);
        if (!CaptureGeometry.IsUsable(Selection)) return null;
        if (mode != CaptureMode.Freeform) return new CaptureSelection(Selection, null, destination);
        return CaptureGeometry.HasPolygonArea(Points)
            ? new CaptureSelection(Selection, Array.AsReadOnly(Points.ToArray()), destination)
            : null;
    }

    // The second click can be retried until the displayed rectangle is large enough.
    internal CaptureSelection? ConfirmTwoPoint(OutputDestination destination) =>
        IsPressed && CaptureGeometry.IsUsable(Selection) ? new(Selection, null, destination) : null;

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
        anchorOffset = OffsetFor(anchor);
        if (mode == CaptureMode.FixedRegion && FixedPosition is null) PositionFixed(Resolve(RawCurrent, previousSnap));
    }

    internal void ResumeFixedPointer()
    {
        anchorOffset = OffsetFor(FixedAnchor);
        pointerInitialized = false;
    }

    private Size OffsetFor(CaptureAnchor anchor) => anchor switch
    {
        CaptureAnchor.TopRight => new(fixedDimensions.Width, 0),
        CaptureAnchor.BottomRight => fixedDimensions,
        CaptureAnchor.BottomLeft => new(0, fixedDimensions.Height),
        CaptureAnchor.Center => new(fixedDimensions.Width / 2, fixedDimensions.Height / 2),
        _ => Size.Empty
    };

    internal bool Space(Func<Point, Point?>? movePointer = null, CaptureAnchorCycle cycle = CaptureAnchorCycle.Legacy)
    {
        if (FixedPosition is not null) return true;
        if (mode == CaptureMode.FixedRegion)
        {
            ChangeAnchor(NextAnchor(FixedAnchor, cycle == CaptureAnchorCycle.Legacy ? CaptureAnchorCycle.CornersAndCenter : cycle));
            return true;
        }
        if (mode != CaptureMode.Rectangle || !IsPressed || !HasDragged || !CaptureGeometry.IsUsable(Selection)) return false;

        CaptureAnchor current = centerMove is not null ? CaptureAnchor.Center : PointerCorner(rectangleBase, Current);
        CaptureAnchor next = NextAnchor(current, cycle == CaptureAnchorCycle.Legacy ? CaptureAnchorCycle.Diagonal : cycle);
        Point target = AnchorPoint(Selection, next);
        Point? pointer = movePointer is null ? target : movePointer(target);
        if (pointer is null) return false;

        ResetRectangleAdjustments();
        rectangleBase = Selection;
        if (next == CaptureAnchor.Center)
            centerMove = (Selection, pointer.Value);
        else
            start = AnchorPoint(Selection, IsCentered ? CaptureAnchor.Center : NextAnchor(next, CaptureAnchorCycle.Diagonal));
        Current = target;
        RawCurrent = pointer.Value;
        swappedPointer = pointer;
        horizontal.Reset();
        vertical.Reset();
        return true;
    }

    internal bool ResizeRectangle(int wheelDelta)
    {
        if (mode != CaptureMode.Rectangle || !IsPressed || !HasDragged || !CaptureGeometry.IsUsable(rectangleBase)) return false;
        (int steps, rectangleWheelRemainder) = CaptureTargetMargin.WheelSteps(rectangleWheelRemainder, wheelDelta);
        if (steps != 0)
        {
            if (HasWheelAspect)
            {
                int offset = ClampOffset(rectangleWheelOffset.Width + steps);
                rectangleWheelOffset = new(offset, offset);
            }
            else
            {
                int x = WheelLocksWidth ? 0 : ClampOffset((Selection.Width - rectangleBase.Width) / 2 + steps);
                int y = WheelLocksHeight ? 0 : ClampOffset((Selection.Height - rectangleBase.Height) / 2 + steps);
                rectangleWheelOffset = new(x, y);
            }
            Selection = AdjustedRectangle();
            if (centerMove is not null) Current = AnchorPoint(Selection, CaptureAnchor.Center);
        }
        return true;
    }

    private bool HasWheelAspect => centerMove is null && (dragAspect.HasValue || AspectRatio != CaptureAspectRatio.Free);
    private bool WheelLocksWidth => centerMove is null && horizontal.IsLocked;
    private bool WheelLocksHeight => centerMove is null && vertical.IsLocked;

    private static int ClampOffset(int offset) => Math.Clamp(offset, -32768, 32768);

    private Rectangle AdjustedRectangle()
    {
        if (rectangleWheelOffset.IsEmpty || desktopBounds is not Rectangle desktop || !CaptureGeometry.IsUsable(rectangleBase))
            return rectangleBase;
        // Keep opposite edges the same distance from the unadjusted rectangle.
        int spaceX = Math.Max(0, Math.Min(rectangleBase.Left - desktop.Left, desktop.Right - rectangleBase.Right));
        int spaceY = Math.Max(0, Math.Min(rectangleBase.Top - desktop.Top, desktop.Bottom - rectangleBase.Bottom));
        if (HasWheelAspect)
        {
            if (WheelLocksWidth || WheelLocksHeight) return rectangleBase;
            return ResizeAtAspect(Rectangle.Inflate(rectangleBase, spaceX, spaceY).Size);
        }

        int x = WheelLocksWidth ? 0 : ClampMargin(rectangleWheelOffset.Width, rectangleBase.Width, spaceX);
        int y = WheelLocksHeight ? 0 : ClampMargin(rectangleWheelOffset.Height, rectangleBase.Height, spaceY);
        return Rectangle.Inflate(rectangleBase, x, y);
    }

    private static int ClampMargin(int requested, int length, int available) =>
        Math.Clamp(requested, -(length - CaptureGeometry.MinimumSize) / 2, available);

    private Rectangle ResizeAtAspect(Size maximumSize)
    {
        bool fixedRatio = AspectRatio != CaptureAspectRatio.Free;
        double ratio = dragAspect.GetValueOrDefault(1);
        bool measureWidth = !fixedRatio && ratio < 1;
        int original = measureWidth ? rectangleBase.Width : rectangleBase.Height;
        int minimum = CaptureGeometry.MinimumSize;
        if (fixedRatio) minimum = Math.Max(minimum, AspectRatios.Units(AspectRatio).Height);
        int adjusted = Math.Max(minimum, original + 2 * rectangleWheelOffset.Width);
        var target = measureWidth ? new Point(adjusted, 0) : new Point(0, adjusted);
        var limits = new Rectangle(Point.Empty, maximumSize);
        Point size = fixedRatio
            ? AspectRatios.Constrain(Point.Empty, target, AspectRatio, limits, false, false)
            : AspectRatios.ConstrainDrag(Point.Empty, target, ratio, limits);
        if (size.X < CaptureGeometry.MinimumSize || size.Y < CaptureGeometry.MinimumSize) return rectangleBase;

        return new Rectangle(rectangleBase.X + (rectangleBase.Width - size.X) / 2,
            rectangleBase.Y + (rectangleBase.Height - size.Y) / 2, size.X, size.Y);
    }

    private void ResetRectangleAdjustments()
    {
        rectangleWheelOffset = Size.Empty;
        rectangleWheelRemainder = 0;
        centerMove = null;
    }

    private static CaptureAnchor NextAnchor(CaptureAnchor current, CaptureAnchorCycle cycle)
    {
        if (current == CaptureAnchor.Center) return CaptureAnchor.TopLeft;
        if (cycle == CaptureAnchorCycle.Diagonal) return (CaptureAnchor)(((int)current + 2) % 4);
        if (current == CaptureAnchor.BottomLeft && cycle == CaptureAnchorCycle.CornersAndCenter) return CaptureAnchor.Center;
        return (CaptureAnchor)(((int)current + 1) % 4);
    }

    private static CaptureAnchor PointerCorner(Rectangle bounds, Point pointer)
    {
        bool right = pointer.X >= bounds.Left + bounds.Width / 2;
        bool bottom = pointer.Y >= bounds.Top + bounds.Height / 2;
        return bottom ? right ? CaptureAnchor.BottomRight : CaptureAnchor.BottomLeft
            : right ? CaptureAnchor.TopRight : CaptureAnchor.TopLeft;
    }

    private static Point AnchorPoint(Rectangle bounds, CaptureAnchor anchor) => anchor switch
    {
        CaptureAnchor.TopRight => new(bounds.Right, bounds.Top),
        CaptureAnchor.BottomRight => new(bounds.Right, bounds.Bottom),
        CaptureAnchor.BottomLeft => new(bounds.Left, bounds.Bottom),
        CaptureAnchor.Center => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
        _ => bounds.Location
    };

    internal CaptureSelection? WindowSelection(Point point, OutputDestination destination) =>
        FromBounds(WindowAt(point), destination) is { } selection ? selection with { WindowHitPoint = point } : null;
    internal Rectangle MonitorAt(Point point) => monitors.FirstOrDefault(monitor => monitor.Contains(point));
    internal CaptureSelection? MonitorSelection(Point point, OutputDestination destination) => FromBounds(MonitorAt(point), destination);

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
        swappedPointer = null;
        ResetRectangleAdjustments();
        dragAspect = null;
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

    private bool IsCentered => rectangleAction is CaptureKeyAction.FromCenter or CaptureKeyAction.FromCenterWithAspectRatio;

    private Point Reflect(Point corner) => new((int)(2L * start.X - corner.X), (int)(2L * start.Y - corner.Y));

    private Rectangle CenteredLimits()
    {
        long left = desktopBounds?.Left ?? int.MinValue, right = desktopBounds?.Right ?? int.MaxValue;
        long top = desktopBounds?.Top ?? int.MinValue, bottom = desktopBounds?.Bottom ?? int.MaxValue;
        int x = (int)Math.Clamp(Math.Min(start.X - left, right - start.X), 0, int.MaxValue / 2);
        int y = (int)Math.Clamp(Math.Min(start.Y - top, bottom - start.Y), 0, int.MaxValue / 2);
        return new Rectangle(start.X - x, start.Y - y, 2 * x, 2 * y);
    }

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
