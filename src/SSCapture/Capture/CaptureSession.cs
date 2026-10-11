namespace SSCapture.Capture;

internal static class CaptureSession
{
    /// <summary>Call on the application's STA UI thread. The caller owns the outcome.</summary>
    public static Task<CaptureOutcome> SelectAsync(CaptureOptions options)
    {
        ValidateCall(options);
        // Freeze before showing any overlay or menu, with no intentional capture delay.
        DesktopSnapshot snapshot = DesktopSnapshot.Take(options.IncludeCursor);
        IReadOnlyList<Rectangle> windows;
        try { windows = CaptureNative.WindowBounds(snapshot.Bounds); }
        catch { snapshot.Dispose(); throw; }
        return SelectAsync(options, snapshot, windows, () => CaptureNative.TakeAfterOverlaysClosed(options.IncludeCursor));
    }

    /// <summary>Consumes snapshot. Never reads desktop pixels or enumerates real windows.</summary>
    internal static Task<CaptureOutcome> SelectAsync(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle>? windows = null, Func<DesktopSnapshot>? currentFrameFactory = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            ValidateCall(options);
            if (!options.FreezeFrame && options.Intent == CaptureIntent.Screenshot && currentFrameFactory is null)
                throw new ArgumentException("画面固定OFFの合成画像検証には画像取得関数を指定してください。", nameof(currentFrameFactory));
            SessionController session = new(options, snapshot, windows ?? [], currentFrameFactory);
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
    private readonly SelectionGesture gesture;
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
    private System.Windows.Forms.Timer? snapTimer;

    internal CaptureMode Mode => options.Mode;
    internal DesktopSnapshot Snapshot { get; }
    internal Task<CaptureOutcome> Completion => completion.Task;
    internal bool Dragging => gesture.IsPressed;
    internal Point Current => gesture.Current;
    internal Rectangle Selection => gesture.Selection;
    internal List<Point> Points => gesture.Points;
    internal OutputDestination Destination { get; private set; }
    internal CaptureContextMenu SelectionMenu { get; }
    internal bool FixedSelectionValid => Mode != CaptureMode.FixedRegion || gesture.FixedSelection(Destination) is not null;

    internal SessionController(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle> windows, Func<DesktopSnapshot>? currentFrameFactory = null)
    {
        this.options = options;
        this.currentFrameFactory = currentFrameFactory;
        Snapshot = snapshot;
        Destination = options.Destination;
        gesture = new SelectionGesture(options.Mode, snapshot.Monitors, windows.ToArray(), options.FixedSize, options.FixedAnchor);
        SelectionMenu = new CaptureContextMenu(Destination, destination => Destination = destination,
            () => FinishCommand(true), Cancel);
        SelectionMenu.Closed += MenuClosed;
    }

    internal void Start()
    {
        try
        {
            if (Mode == CaptureMode.FullScreen)
            {
                CompleteSelection(new CaptureSelection(Snapshot.Bounds, null, Destination));
                return;
            }
            foreach (Rectangle bounds in Snapshot.Monitors) overlays.Add(new CaptureOverlay(this, bounds));
            foreach (CaptureOverlay overlay in overlays) overlay.Show();
            MoveTo(CaptureNative.CursorPosition(), CaptureNative.AltPressed());
            CaptureOverlay active = overlays.FirstOrDefault(overlay => overlay.MonitorBounds.Contains(gesture.RawCurrent)) ?? overlays[0];
            active.Activate();
            active.Focus();
            guide = new UI.SelectionGuideForm(active.MonitorBounds, options.Intent, Cancel,
                Mode == CaptureMode.FixedRegion ? options.FixedSize : null, HandleCommandKey, options.FixedAnchor, AltKeyChanged);
            guide.Show(active);
            active.Activate();
            active.Focus();
            starting = false;
            if (Mode is CaptureMode.Rectangle or CaptureMode.FixedRegion)
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
            MoveTo(CaptureNative.CursorPosition(), CaptureNative.AltPressed(queuedKeyState));
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void MoveTo(Point physicalPosition, bool snap = false)
    {
        if (completed || menuOpen || menuTransition) return;
        // Keyboard nudges must survive synthetic mouse messages at the unchanged cursor
        // location, including the click used to confirm an adjusted fixed rectangle.
        if (Mode == CaptureMode.FixedRegion && lastPointerPosition == physicalPosition && lastSnap == snap) return;
        lastPointerPosition = physicalPosition;
        lastSnap = snap;
        gesture.Move(physicalPosition, snap);
        RefreshOverlays();
    }

    internal void ModifiersChanged() => UpdateModifiers(false);
    internal void AltKeyChanged() => UpdateModifiers(true);

    private void UpdateModifiers(bool queuedKeyState)
    {
        if (completed || menuOpen || menuTransition || Mode is not (CaptureMode.Rectangle or CaptureMode.FixedRegion)) return;
        try
        {
            bool snap = CaptureNative.AltPressed(queuedKeyState);
            if (snap != lastSnap && lastPointerPosition is Point point) MoveTo(point, snap);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal bool HandleCommandKey(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape) { Cancel(); return true; }
        if (completed || menuOpen || menuTransition || Mode != CaptureMode.FixedRegion) return false;
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
        foreach (CaptureOverlay overlay in overlays) overlay.Invalidate();
    }

    internal void MouseDown(CaptureOverlay overlay, MouseButtons button)
    {
        if (completed || menuOpen || menuTransition) return;
        if (button == MouseButtons.Right) { ShowMenu(overlay); return; }
        if (Mode == CaptureMode.FixedRegion && button == MouseButtons.Middle) return;
        if (button is not (MouseButtons.Left or MouseButtons.Middle) || pressedButton != MouseButtons.None) return;
        Move(true);
        if (completed) return;
        dragOwner = overlay;
        pressedButton = button;
        if (button == MouseButtons.Left) gesture.Begin(gesture.RawCurrent, CaptureNative.DragThreshold(overlay.DeviceDpi), lastSnap);
        overlay.Capture = true;
        if (!overlay.Capture) Cancel();
    }

    internal void MouseUp(MouseButtons button)
    {
        if (completed || menuOpen || menuTransition || button != pressedButton || pressedButton == MouseButtons.None) return;
        Move(true);
        if (completed) return;
        CaptureSelection? selection = button == MouseButtons.Middle
            ? gesture.MonitorSelection(gesture.RawCurrent, Destination)
            : gesture.End(gesture.RawCurrent, Destination, lastSnap);
        ReleasePointer();
        if (selection is null && Mode != CaptureMode.FixedRegion) Cancel();
        else if (selection is null) RefreshOverlays();
        else CompleteSelection(selection);
    }

    private void ShowMenu(CaptureOverlay overlay)
    {
        try
        {
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
        if (e.CloseReason is ToolStripDropDownCloseReason.AppFocusChange or ToolStripDropDownCloseReason.Keyboard)
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
        snapTimer?.Stop();
        snapTimer?.Dispose();
        snapTimer = null;
        ReleasePointer();
        gesture.Reset();
        SelectionMenu.Dispose();
        guide?.Dispose();
        foreach (CaptureOverlay overlay in overlays) overlay.Dispose();
    }

    internal void CompleteSelection(CaptureSelection selection)
    {
        if (completed) return;
        if (Mode == CaptureMode.FixedRegion && (selection.Polygon is not null
            || selection.Bounds.Size != options.FixedSize || !CaptureGeometry.IsFullyCovered(selection.Bounds, Snapshot.Monitors))) return;
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
