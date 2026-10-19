namespace SSCapture.Capture;

internal static class CaptureSession
{
    /// <summary>Call on the application's STA UI thread. The caller owns the outcome.</summary>
    public static Task<CaptureOutcome> SelectAsync(CaptureOptions options, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null)
    {
        ValidateCall(options);
        Point initialPointer = CaptureNative.CursorPosition();
        // Freeze before showing any overlay or menu, with no intentional capture delay.
        DesktopSnapshot snapshot = DesktopSnapshot.Take(options.IncludeCursor);
        IReadOnlyList<Rectangle> windows;
        try { windows = CaptureNative.WindowBounds(snapshot.Bounds); }
        catch { snapshot.Dispose(); throw; }
        return SelectAsync(options, snapshot, windows, () => CaptureNative.TakeAfterOverlaysClosed(options.IncludeCursor), initialPointer, gridChanged, guideChanged);
    }

    /// <summary>Consumes snapshot. Never reads desktop pixels or enumerates real windows.</summary>
    internal static Task<CaptureOutcome> SelectAsync(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle>? windows = null, Func<DesktopSnapshot>? currentFrameFactory = null, Point? initialPointer = null, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            ValidateCall(options);
            if (!options.FreezeFrame && options.Intent == CaptureIntent.Screenshot && currentFrameFactory is null)
                throw new ArgumentException("画面固定OFFの合成画像検証には画像取得関数を指定してください。", nameof(currentFrameFactory));
            SessionController session = new(options, snapshot, windows ?? [], currentFrameFactory,
                initialPointer ?? snapshot.Monitors.FirstOrDefault().Location, gridChanged: gridChanged, guideChanged: guideChanged);
            session.Start();
            return session.Completion;
        }
        catch { snapshot.Dispose(); throw; }
    }

    // Compatibility entry points: return an independently owned bitmap as before.
    public static async Task<Bitmap?> CaptureAsync(CaptureMode mode)
    {
        using CaptureOutcome outcome = await SelectAsync(new CaptureOptions(mode, CaptureIntent.Screenshot, OutputDestination.Clipboard));
        return outcome.Image is null ? null : (Bitmap)outcome.Image.Clone();
    }

    internal static async Task<Bitmap?> CaptureAsync(CaptureMode mode, DesktopSnapshot snapshot)
    {
        using CaptureOutcome outcome = await SelectAsync(new CaptureOptions(mode, CaptureIntent.Screenshot, OutputDestination.Clipboard), snapshot);
        return outcome.Image is null ? null : (Bitmap)outcome.Image.Clone();
    }

    private static void ValidateCall(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("撮影は STA UI スレッドで開始してください。");
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Intent) || !Enum.IsDefined(options.FixedAnchor)
            || (options.Destination & ~(OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp)) != 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Mode == CaptureMode.FixedRegion) CaptureGeometry.ValidateFixedSize(options.FixedSize);
    }
}

internal sealed class SessionController
{
    private readonly TaskCompletionSource<CaptureOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CaptureOverlay> overlays = [];
    private readonly CaptureOptions options;
    private readonly Func<DesktopSnapshot>? currentFrameFactory;
    private SelectionGesture gesture;
    private readonly IReadOnlyList<Rectangle> windows;
    private readonly Action<bool>? gridChanged;
    private readonly Action<bool>? guideChanged;
    internal bool ShowSelectionGuide { get; private set; }
    private Point menuPointer;
    private readonly Point initialPointer;
    private readonly Rectangle? selectionBounds;
    private readonly Func<Rectangle, IDisposable> confineCursor;
    private IDisposable? cursorConfinement;
    private CaptureOverlay? dragOwner;
    private CaptureOverlay? menuOwner;
    private UI.SelectionGuideForm? guide;
    private MouseButtons pressedButton;
    private bool completed;
    private bool starting = true;
    private bool menuTransition;
    private bool menuOpen;
    private Point? lastPointerPosition;
    private bool lastSnap;
    private bool lastHeightLock, lastWidthLock;
    private Keys ignoredModifiers;
    private MouseButtons suppressedButtons;
    private System.Windows.Forms.Timer? snapTimer;

    internal CaptureMode Mode { get; private set; }
    internal bool Grid20 => gesture.Grid20;
    internal CaptureAnchor FixedAnchor => gesture.FixedAnchor;
    internal CaptureAspectRatio AspectRatio { get; private set; }
    internal DesktopSnapshot Snapshot { get; }
    internal Task<CaptureOutcome> Completion => completion.Task;
    internal bool Dragging => gesture.IsPressed;
    internal Point Current => gesture.Current;
    internal Rectangle Selection => gesture.Selection;
    internal List<Point> Points => gesture.Points;
    internal OutputDestination Destination { get; private set; }
    internal IReadOnlyList<Rectangle> SelectionMonitors { get; }
    internal CaptureContextMenu SelectionMenu { get; }
    internal bool FixedSelectionValid => Mode != CaptureMode.FixedRegion || gesture.FixedSelection(Destination) is not null;

    internal SessionController(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle> windows, Func<DesktopSnapshot>? currentFrameFactory = null,
        Point? initialPointer = null, Func<Rectangle, IDisposable>? confineCursor = null, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null)
    {
        this.options = options;
        Mode = options.Mode;
        this.gridChanged = gridChanged;
        this.guideChanged = guideChanged;
        ShowSelectionGuide = options.ShowSelectionGuide;
        this.currentFrameFactory = currentFrameFactory;
        this.initialPointer = initialPointer ?? snapshot.Monitors[0].Location;
        this.confineCursor = confineCursor ?? CursorConfinement.Acquire;
        Snapshot = snapshot;
        Destination = options.Destination;
        if (options.LimitToStartingMonitor && options.Mode != CaptureMode.FullScreen)
        {
            selectionBounds = snapshot.Monitors.FirstOrDefault(monitor => monitor.Contains(this.initialPointer));
            if (selectionBounds.Value.IsEmpty) throw new InvalidOperationException("開始位置のモニタが見つかりません。もう一度選択してください。");
            SelectionMonitors = [selectionBounds.Value];
            windows = windows.Select(window => Rectangle.Intersect(window, selectionBounds.Value))
                .Where(CaptureGeometry.IsUsable).ToArray();
        }
        else SelectionMonitors = snapshot.Monitors;
        this.windows = windows.ToArray();
        gesture = CreateGesture(Mode, options.Grid20);
        SelectionMenu = new CaptureContextMenu(Destination, destination => Destination = destination,
            () => FinishCommand(true), Cancel, Mode, options.Grid20, ChangeMode, ChangeGrid20, () => CaptureDesktop(menuPointer),
            showGuide: ShowSelectionGuide, changeGuide: ChangeGuideVisibility, changeRatio: ChangeAspectRatio);
        SelectionMenu.SetRatio(AspectRatio);
        SelectionMenu.Closed += MenuClosed;
    }

    private SelectionGesture CreateGesture(CaptureMode mode, bool grid20) =>
        new(mode, SelectionMonitors, windows, options.FixedSize ?? new Size(1280, 720), options.FixedAnchor) { Grid20 = grid20, AspectRatio = AspectRatio };

    internal void ChangeAspectRatio(CaptureAspectRatio ratio)
    {
        if (!Enum.IsDefined(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio));
        AspectRatio = ratio; gesture.AspectRatio = ratio;
        SelectionMenu.SetRatio(ratio);
        RefreshOverlays();
    }

    internal void ChangeGrid20(bool enabled)
    {
        try
        {
            gridChanged?.Invoke(enabled);
            gesture.Grid20 = enabled;
            SelectionMenu.SetGrid(enabled);
            if (lastPointerPosition is Point point) gesture.Move(point, lastSnap, lastHeightLock, lastWidthLock);
            RefreshOverlays();
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void ChangeMode(CaptureMode mode)
    {
        if (completed || mode == Mode) return;
        try
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            bool grid20 = Grid20;
            gesture.Reset();
            ReleasePointer();
            Mode = mode;
            gesture = CreateGesture(mode, grid20);
            SelectionMenu.SetMode(mode);
            SelectionMenu.SetRatio(AspectRatio);
            if (SelectionMenu.Visible) SelectionMenu.Close(ToolStripDropDownCloseReason.ItemClicked);
            if (mode == CaptureMode.FullScreen)
            {
                CompleteSelection(new CaptureSelection(selectionBounds ?? Snapshot.Bounds, null, Destination));
                return;
            }
            lastPointerPosition = null;
            gesture.Move(menuOpen ? menuPointer : initialPointer);
            ReplaceGuide();
            RefreshOverlays();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void ReplaceGuide()
    {
        guide?.Dispose();
        guide = null;
        if (overlays.Count == 0 || !ShowSelectionGuide) return;
        CaptureOverlay owner = menuOwner ?? overlays.FirstOrDefault(o => o.MonitorBounds.Contains(gesture.RawCurrent)) ?? overlays[0];
        guide = new UI.SelectionGuideForm(owner.MonitorBounds, options.Intent, Cancel,
            Mode == CaptureMode.FixedRegion ? options.FixedSize ?? new Size(1280, 720) : null, HandleCommandKey, options.FixedAnchor, AltKeyChanged, Mode,
            dismiss: () => ChangeGuideVisibility(false));
        guide.Show(owner);
        UpdateGuidePointer();
        owner.BeginInvoke((Action)(() =>
        {
            if (completed || owner.IsDisposed || menuOpen || !CaptureNative.OwnsForegroundWindow()) return;
            owner.Activate();
            owner.Focus();
        }));
    }

    internal void ChangeGuideVisibility(bool enabled)
    {
        if (completed || enabled == ShowSelectionGuide) return;
        try { guideChanged?.Invoke(enabled); }
        catch (Exception ex)
        {
            guide?.ShowSaveError(ex.Message);
            return;
        }
        ShowSelectionGuide = enabled;
        SelectionMenu.SetGuide(enabled);
        if (enabled) ReplaceGuide(); else guide?.Hide();
    }

    private void UpdateGuidePointer()
    {
        if (ShowSelectionGuide) guide?.UpdatePointer(gesture.RawCurrent, gesture.IsPressed && gesture.HasDragged);
    }

    internal void CaptureDesktop(Point point)
    {
        CaptureSelection? selection = gesture.MonitorSelection(point, Destination);
        if (selection is not null) CompleteSelection(selection, entireMonitor: true);
    }

    internal void Start()
    {
        try
        {
            ignoredModifiers = CaptureNative.Modifiers() & (Keys.Shift | Keys.Control);
            if (Mode == CaptureMode.FullScreen)
            {
                CompleteSelection(new CaptureSelection(Snapshot.Bounds, null, Destination));
                return;
            }
            foreach (Rectangle bounds in SelectionMonitors) overlays.Add(new CaptureOverlay(this, bounds));
            foreach (CaptureOverlay overlay in overlays) overlay.Show();
            MoveTo(initialPointer, CaptureNative.AltPressed());
            CaptureOverlay active = overlays.FirstOrDefault(overlay => overlay.MonitorBounds.Contains(gesture.RawCurrent)) ?? overlays[0];
            active.Activate();
            active.Focus();
            ReplaceGuide();
            active.Activate();
            active.Focus();
            starting = false;
            if (selectionBounds is Rectangle monitorBounds) cursorConfinement = confineCursor(monitorBounds);
            if (Mode != CaptureMode.FullScreen)
            {
                // Key messages can be routed to the movable guide or a captured
                // overlay. Only while selecting, poll Alt to cover either route.
                snapTimer = new System.Windows.Forms.Timer { Interval = 16 };
                snapTimer.Tick += (_, _) => ModifiersChanged();
                snapTimer.Start();
            }
            Move();
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void Move(bool queuedKeyState = false)
    {
        if (completed || menuOpen || menuTransition) return;
        try
        {
            Keys modifiers = ReadModifiers(queuedKeyState);
            MoveTo(CaptureNative.CursorPosition(), (modifiers & Keys.Alt) != 0,
                (modifiers & Keys.Shift) != 0, (modifiers & Keys.Control) != 0);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void MoveTo(Point physicalPosition, bool snap = false, bool lockHeight = false, bool lockWidth = false)
    {
        if (completed || menuOpen || menuTransition) return;
        if (selectionBounds is Rectangle bounds)
            physicalPosition = new Point(Math.Clamp(physicalPosition.X, bounds.Left, bounds.Right - 1),
                Math.Clamp(physicalPosition.Y, bounds.Top, bounds.Bottom - 1));
        // Keyboard nudges must survive synthetic mouse messages at the unchanged cursor
        // location, including the click used to confirm an adjusted fixed rectangle.
        if (Mode == CaptureMode.FixedRegion && lastPointerPosition == physicalPosition && lastSnap == snap) return;
        lastPointerPosition = physicalPosition;
        lastSnap = snap;
        lastHeightLock = lockHeight;
        lastWidthLock = lockWidth;
        gesture.Move(physicalPosition, snap, lockHeight, lockWidth);
        RefreshOverlays();
    }

    internal void ModifiersChanged() => UpdateModifiers(false);
    internal void AltKeyChanged() => UpdateModifiers(true);

    private void UpdateModifiers(bool queuedKeyState)
    {
        if (completed || menuOpen || menuTransition) return;
        try
        {
            if (Control.MouseButtons == MouseButtons.None) suppressedButtons = MouseButtons.None;
            Keys modifiers = ReadModifiers(queuedKeyState);
            bool snap = (modifiers & Keys.Alt) != 0, height = (modifiers & Keys.Shift) != 0, width = (modifiers & Keys.Control) != 0;
            if ((snap != lastSnap || height != lastHeightLock || width != lastWidthLock) && lastPointerPosition is Point point)
                MoveTo(point, snap, height, width);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal bool HandleCommandKey(Keys keyData) => HandleCommandKey(keyData, false);

    internal bool HandleCommandKey(Keys keyData, bool repeated)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            if (gesture.IsPressed) AbortSelection(); else Cancel();
            return true;
        }
        if (completed || menuOpen || menuTransition) return false;
        if ((keyData & Keys.KeyCode) == Keys.Space && (keyData & Keys.Alt) == 0)
        {
            if (repeated) return true;
            bool handled = gesture.Space();
            if (handled) { guide?.UpdateAnchor(gesture.FixedAnchor); RefreshOverlays(); }
            return handled;
        }
        if (Mode != CaptureMode.FixedRegion) return false;
        if (keyData == Keys.Enter)
        {
            CaptureSelection? selection = gesture.FixedSelection(Destination);
            if (selection is not null) CompleteSelection(selection);
            return true;
        }
        if (!gesture.Nudge(keyData)) return false;
        RefreshOverlays();
        return true;
    }

    private void RefreshOverlays()
    {
        UpdateGuidePointer();
        foreach (CaptureOverlay overlay in overlays) overlay.Invalidate();
    }

    internal void MouseDown(CaptureOverlay overlay, MouseButtons button)
    {
        if (completed || menuOpen || menuTransition) return;
        if (suppressedButtons != MouseButtons.None) return;
        if (button == MouseButtons.Right)
        {
            if (gesture.IsPressed) { AbortSelection(); suppressedButtons |= MouseButtons.Right; overlay.Capture = false; }
            else ShowMenu(overlay);
            return;
        }
        if (button is not (MouseButtons.Left or MouseButtons.Middle) || pressedButton != MouseButtons.None) return;
        Move(true);
        if (completed) return;
        dragOwner = overlay;
        pressedButton = button;
        if (button == MouseButtons.Left) gesture.Begin(gesture.RawCurrent, CaptureNative.DragThreshold(overlay.DeviceDpi), lastSnap, lastHeightLock, lastWidthLock);
        overlay.Capture = true;
        if (!overlay.Capture) Cancel();
    }

    internal void MouseUp(MouseButtons button)
    {
        if ((suppressedButtons & button) != 0) { suppressedButtons &= ~button; return; }
        if (completed || menuOpen || menuTransition || button != pressedButton || pressedButton == MouseButtons.None) return;
        Move(true);
        if (completed) return;
        CaptureSelection? selection = button == MouseButtons.Middle
            ? gesture.MonitorSelection(gesture.RawCurrent, Destination)
            : gesture.End(gesture.RawCurrent, Destination, lastSnap, lastHeightLock, lastWidthLock);
        ReleasePointer();
        if (selection is null) { gesture.Reset(); RefreshOverlays(); }
        else CompleteSelection(selection, entireMonitor: button == MouseButtons.Middle);
    }

    private Keys ReadModifiers(bool queuedKeyState)
    {
        Keys modifiers = CaptureNative.Modifiers(queuedKeyState);
        ignoredModifiers &= modifiers;
        return modifiers & ~ignoredModifiers;
    }

    internal void AbortSelection()
    {
        suppressedButtons |= pressedButton;
        gesture.Reset();
        ReleasePointer();
        RefreshOverlays();
    }

    private void ShowMenu(CaptureOverlay overlay)
    {
        try
        {
            Move(true);
            menuPointer = gesture.RawCurrent;
            menuTransition = true;
            gesture.Reset();
            ReleasePointer();
            // WinForms may have implicitly captured the right button before OnMouseDown.
            overlay.Capture = false;
            foreach (CaptureOverlay form in overlays) form.Invalidate();
            menuOwner = overlay;
            menuOpen = true;
            SelectionMenu.Show(overlay, overlay.PointToClient(CaptureNative.CursorPosition()));
            menuTransition = false;
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void MenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        menuOpen = false;
        if (completed) return;
        menuTransition = true;
        // Popup activation can transiently clear GetForegroundWindow. Check focus
        // after the closing input message instead of treating that transition as exit.
        if (e.CloseReason == ToolStripDropDownCloseReason.Keyboard)
        {
            Cancel();
            return;
        }
        CaptureOverlay? owner = menuOwner;
        if (owner is null || owner.IsDisposed) { Cancel(); return; }
        // Keep the closing menu's mouse message from becoming a selection gesture.
        owner.BeginInvoke((Action)(() =>
        {
            if (completed || owner.IsDisposed) return;
            if (!CaptureNative.OwnsForegroundWindow()) { Cancel(); return; }
            menuTransition = false;
            owner.Activate();
            owner.Focus();
            Move();
        }));
    }

    internal void CaptureLost(CaptureOverlay overlay)
    {
        if (completed || menuOpen || menuTransition || pressedButton == MouseButtons.None || overlay != dragOwner || overlay.IsDisposed) return;
        try
        {
            overlay.BeginInvoke((Action)(() =>
            {
                if (!completed && !menuOpen && !menuTransition && pressedButton != MouseButtons.None && !overlay.IsDisposed && !overlay.Capture) Cancel();
            }));
        }
        catch (InvalidOperationException) { Cancel(); }
    }

    internal void ApplicationDeactivated()
    {
        if (!starting && !menuOpen && !menuTransition) Cancel();
    }

    internal void Cancel() => FinishCommand(false);

    private void ReleasePointer()
    {
        pressedButton = MouseButtons.None;
        CaptureOverlay? owner = dragOwner;
        dragOwner = null;
        if (owner is not null && !owner.IsDisposed) owner.Capture = false;
    }

    private void CloseOverlays()
    {
        try
        {
            snapTimer?.Stop();
            snapTimer?.Dispose();
            snapTimer = null;
            ReleasePointer();
            gesture.Reset();
            SelectionMenu.Dispose();
            guide?.Dispose();
            foreach (CaptureOverlay overlay in overlays) overlay.Dispose();
        }
        finally
        {
            IDisposable? confinement = cursorConfinement;
            cursorConfinement = null;
            confinement?.Dispose();
        }
    }

    internal void CompleteSelection(CaptureSelection selection, bool entireMonitor = false)
    {
        if (completed) return;
        if (selectionBounds is Rectangle bounds && (!bounds.Contains(selection.Bounds)
            || selection.Polygon?.Any(point => !bounds.Contains(point)) == true)) return;
        Size fixedSize = options.FixedSize ?? new Size(1280, 720);
        if (Mode == CaptureMode.FixedRegion && !entireMonitor && (selection.Polygon is not null
            || selection.Bounds.Size != fixedSize || !CaptureGeometry.IsFullyCovered(selection.Bounds, SelectionMonitors))) return;
        completed = true;
        Bitmap? result = null;
        try
        {
            CloseOverlays();
            if (options.Intent == CaptureIntent.Screenshot || options.IncludeRecordingImage)
            {
                using DesktopSnapshot? current = options.FreezeFrame || options.Intent == CaptureIntent.Recording ? null : currentFrameFactory!();
                DesktopSnapshot frame = current ?? Snapshot;
                // A display-layout change must not silently shift the chosen physical region.
                if (!frame.Bounds.Contains(selection.Bounds))
                    throw new InvalidOperationException("選択中にディスプレイ領域が変わりました。もう一度撮影してください。");
                if (Mode == CaptureMode.FixedRegion && !CaptureGeometry.IsFullyCovered(selection.Bounds, frame.Monitors))
                    throw new InvalidOperationException("選択中にディスプレイ配置が変わりました。もう一度撮影してください。");
                result = selection.Polygon is { } polygon
                    ? CaptureGeometry.Freeform(frame.Image, frame.Bounds, polygon)
                    : CaptureGeometry.Crop(frame.Image, frame.Bounds, selection.Bounds, Mode == CaptureMode.FixedRegion ? 1 : CaptureGeometry.MinimumSize);
                if (result is null) { completion.TrySetResult(new CaptureOutcome(null, null)); return; }
            }
            completion.TrySetResult(new CaptureOutcome(result, selection));
            result = null;
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { result?.Dispose(); Snapshot.Dispose(); }
    }

    private void FinishCommand(bool openSettings)
    {
        if (completed) return;
        completed = true;
        try
        {
            CloseOverlays();
            completion.TrySetResult(new CaptureOutcome(null, null, openSettings));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { Snapshot.Dispose(); }
    }

    private void Fail(Exception exception)
    {
        if (completed) return;
        completed = true;
        try { CloseOverlays(); }
        finally { Snapshot.Dispose(); completion.TrySetException(exception); }
    }
}
