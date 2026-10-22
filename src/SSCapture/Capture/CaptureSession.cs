namespace SSCapture.Capture;

internal static class CaptureSession
{
    /// <summary>Call on the application's STA UI thread. The caller owns the outcome.</summary>
    public static async Task<CaptureOutcome> SelectAsync(CaptureOptions options, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null)
    {
        ValidateCall(options);
        Point initialPointer = CaptureNative.CursorPosition();
        // Freeze before showing any overlay or menu, with no intentional capture delay.
        bool metadata = options.Intent == CaptureIntent.Screenshot && options.OutputSettings is { } settings && Services.CaptureMetadata.RequiresPreparation(settings);
        DesktopSnapshot snapshot = DesktopSnapshot.Take(options.IncludeCursor, metadata);
        if (metadata)
            snapshot.Context = await Services.CaptureContextClient.Shared.ObserveAsync(new Point(snapshot.Pointer!.Screen.X, snapshot.Pointer.Screen.Y));
        IReadOnlyList<Rectangle> windows;
        try { windows = CaptureNative.WindowBounds(snapshot.Bounds); }
        catch { snapshot.Dispose(); throw; }
        return await SelectAsync(options, snapshot, windows, () =>
        {
            CaptureNative.SynchronizeDesktop();
            return DesktopSnapshot.Take(options.IncludeCursor, metadata);
        }, initialPointer, gridChanged, guideChanged, (cursor, observe) =>
        {
            CaptureNative.SynchronizeDesktop();
            return DesktopSnapshot.Take(cursor, observe);
        }, CaptureNative.WindowBounds);
    }

    /// <summary>Consumes snapshot. Never reads desktop pixels or enumerates real windows.</summary>
    internal static Task<CaptureOutcome> SelectAsync(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle>? windows = null, Func<DesktopSnapshot>? currentFrameFactory = null, Point? initialPointer = null, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null,
        Func<bool, bool, DesktopSnapshot>? recapture = null, Func<Rectangle, IReadOnlyList<Rectangle>>? readWindows = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            ValidateCall(options);
            if (!options.FreezeFrame && options.Intent == CaptureIntent.Screenshot && currentFrameFactory is null && recapture is null)
                throw new ArgumentException("画面固定OFFの合成画像検証には画像取得関数を指定してください。", nameof(currentFrameFactory));
            SessionController session = new(options, snapshot, windows ?? [], currentFrameFactory,
                initialPointer ?? snapshot.Monitors.FirstOrDefault().Location, gridChanged: gridChanged, guideChanged: guideChanged, recapture: recapture, readWindows: readWindows);
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
    internal SSCapture.Services.AppSettings OutputSettings { get; private set; }
    private readonly Func<DesktopSnapshot>? currentFrameFactory;
    private readonly Func<bool, bool, DesktopSnapshot>? recapture;
    private readonly Func<Rectangle, IReadOnlyList<Rectangle>>? readWindows;
    private bool refreshing;
    private CaptureAnchor activeAnchor;
    private SelectionGesture gesture;
    private IReadOnlyList<Rectangle> windows;
    private readonly Action<bool>? gridChanged;
    private readonly Action<bool>? guideChanged;
    internal bool ShowSelectionGuide { get; private set; }
    private Point menuPointer;
    private readonly Point initialPointer;
    private Rectangle? selectionBounds;
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
    private Size activeFixedSize;
    private readonly Func<IReadOnlyList<Services.DisplayLayout>> readPresetDisplays;
    private MouseButtons suppressedButtons;
    private System.Windows.Forms.Timer? snapTimer;

    internal CaptureMode Mode { get; private set; }
    internal bool Grid20 => gesture.Grid20;
    internal CaptureAnchor FixedAnchor => gesture.FixedAnchor;
    internal CaptureAspectRatio AspectRatio { get; private set; }
    internal DesktopSnapshot Snapshot { get; private set; }
    internal Task<CaptureOutcome> Completion => completion.Task;
    internal bool Dragging => gesture.IsPressed;
    internal Point Current => gesture.Current;
    internal Rectangle Selection => gesture.Selection;
    internal List<Point> Points => gesture.Points;
    internal OutputDestination Destination { get; private set; }
    internal IReadOnlyList<Rectangle> SelectionMonitors { get; private set; }
    internal CaptureContextMenu SelectionMenu { get; }
    internal bool FixedSelectionValid => Mode != CaptureMode.FixedRegion || gesture.FixedSelection(Destination) is not null;
    internal bool DimCaptureBackground => OutputSettings.DimCaptureBackground;
    internal bool Refreshing => refreshing;
    internal string? SelectionError { get; private set; }

    internal SessionController(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle> windows, Func<DesktopSnapshot>? currentFrameFactory = null,
        Point? initialPointer = null, Func<Rectangle, IDisposable>? confineCursor = null, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null,
        Func<IReadOnlyList<Services.DisplayLayout>>? readPresetDisplays = null,
        Func<bool, bool, DesktopSnapshot>? recapture = null, Func<Rectangle, IReadOnlyList<Rectangle>>? readWindows = null)
    {
        this.options = options;
        this.recapture = recapture; this.readWindows = readWindows;
        activeAnchor = options.FixedAnchor;
        activeFixedSize = options.FixedSize ?? new Size(1280, 720);
        this.readPresetDisplays = readPresetDisplays ?? Services.RegionCoordinates.ReadDisplays;
        Mode = options.Mode;
        this.gridChanged = gridChanged;
        this.guideChanged = guideChanged;
        ShowSelectionGuide = options.ShowSelectionGuide;
        this.currentFrameFactory = currentFrameFactory;
        this.initialPointer = initialPointer ?? snapshot.Monitors[0].Location;
        this.confineCursor = confineCursor ?? CursorConfinement.Acquire;
        Snapshot = snapshot;
        Destination = options.Destination;
        OutputSettings = (options.OutputSettings ?? new SSCapture.Services.AppSettings()) with
        { FreezeFrame = options.FreezeFrame, IncludeCursor = options.IncludeCursor, DimCaptureBackground = options.DimCaptureBackground,
            LimitToStartingMonitor = options.LimitToStartingMonitor };
        if (options.LimitToStartingMonitor && options.Mode != CaptureMode.FullScreen)
        {
            selectionBounds = snapshot.Monitors.FirstOrDefault(monitor => monitor.Contains(this.initialPointer));
            if (selectionBounds.Value.IsEmpty) throw new InvalidOperationException("開始位置のモニタが見つかりません。もう一度選択してください。");
            SelectionMonitors = [selectionBounds.Value];
        }
        else SelectionMonitors = snapshot.Monitors;
        this.windows = windows.ToArray();
        gesture = CreateGesture(Mode, options.Grid20);
        SelectionMenu = new CaptureContextMenu(Destination, destination => Destination = destination,
            () => FinishCommand(true), Cancel, Mode, options.Grid20, ChangeMode, ChangeGrid20, () => CaptureDesktop(menuPointer),
            showGuide: ShowSelectionGuide, changeGuide: ChangeGuideVisibility, changeRatio: ChangeAspectRatio,
            defaultPadding: OutputSettings.ScreenshotPaddingPixels, changePadding: options.Intent == CaptureIntent.Screenshot ? ChangePadding : null,
            presets: OutputSettings.CapturePresets, applyPreset: ApplyPreset,
            applications: options.Intent == CaptureIntent.Screenshot ? OutputSettings.ImageEditors : OutputSettings.VideoEditors,
            selectedApplication: options.Intent == CaptureIntent.Screenshot ? OutputSettings.SelectedImageEditorId : OutputSettings.SelectedVideoEditorId,
            changeApplication: ChangeApplication, inputPresets: OutputSettings.SavedRegions, applyInputPreset: ApplyInputPreset,
            editing: new(OutputSettings, activeFixedSize, activeAnchor, selectionBounds is not null, options.Intent == CaptureIntent.Screenshot,
                recapture is not null || currentFrameFactory is not null, ChangeFixedSize, ChangeFixedAnchor, ChangeMonitorLimit, ChangeColor,
                mode => _ = ChangeMetadataAsync(mode), ChangeDim, enabled => _ = ChangeCaptureSourceAsync(enabled, OutputSettings.IncludeCursor),
                enabled => _ = ChangeCaptureSourceAsync(OutputSettings.FreezeFrame, enabled)));
        SelectionMenu.SetRatio(AspectRatio);
        SelectionMenu.Closed += MenuClosed;
    }

    private SelectionGesture CreateGesture(CaptureMode mode, bool grid20)
    {
        var next = new SelectionGesture(mode, SelectionMonitors, windows, activeFixedSize, activeAnchor) { Grid20 = grid20, AspectRatio = AspectRatio, WindowLimit = selectionBounds };
        next.SetSnapPrecision(OutputSettings.SnapDistancePixels, OutputSettings.GridStepPixels);
        return next;
    }
    internal void ChangeSnapPrecision(int distance, int step)
    {
        gesture.SetSnapPrecision(distance, step);
        OutputSettings = OutputSettings with { SnapDistancePixels = distance, GridStepPixels = step };
        UpdateMenu(); RefreshOverlays();
    }

    private void UpdateMenu() => SelectionMenu.UpdateEditing(OutputSettings, activeFixedSize, activeAnchor, selectionBounds is not null);
    internal void ChangeColor(Services.ScreenshotColorDepth depth)
    {
        if (!Enum.IsDefined(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        OutputSettings = OutputSettings with { ScreenshotColorDepth = depth }; UpdateMenu();
    }
    internal void ChangeDim(bool enabled)
    { OutputSettings = OutputSettings with { DimCaptureBackground = enabled }; UpdateMenu(); RefreshOverlays(); }
    internal void ChangeFixedAnchor(CaptureAnchor anchor)
    {
        if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
        activeAnchor = anchor; gesture.ChangeAnchor(anchor); guide?.UpdateAnchor(anchor); UpdateMenu(); RefreshOverlays();
    }
    internal void ChangeFixedSize(Size size)
    {
        CaptureGeometry.ValidateFixedSize(size);
        if (completed || refreshing) return;
        Rectangle previous = gesture.Selection;
        bool wasFixed = Mode == CaptureMode.FixedRegion, grid = Grid20;
        activeFixedSize = size; Mode = CaptureMode.FixedRegion;
        ReleasePointer(); gesture = CreateGesture(Mode, grid);
        Point pointer = lastPointerPosition ?? initialPointer;
        if (wasFixed) gesture.PlaceFixed(new Rectangle(previous.Location, size), pointer); else gesture.Move(pointer);
        SelectionMenu.SetMode(Mode); UpdateMenu(); ReplaceGuide(); RefreshOverlays();
        SelectionMenu.CloseForCapture();
    }
    internal void ChangeMonitorLimit(bool enabled)
    {
        if (completed || refreshing) return;
        Rectangle? limit = null;
        if (enabled)
        {
            Point pointer = menuOpen ? menuPointer : lastPointerPosition ?? initialPointer;
            Rectangle monitor = Snapshot.Monitors.FirstOrDefault(m => m.Contains(pointer));
            if (monitor.IsEmpty) throw new InvalidOperationException("ポインタのあるモニタが見つかりません。");
            limit = monitor;
        }
        cursorConfinement?.Dispose(); cursorConfinement = null;
        selectionBounds = limit; SelectionMonitors = limit is Rectangle limited ? [limited] : Snapshot.Monitors;
        gesture.UpdateTargets(SelectionMonitors, windows, limit);
        OutputSettings = OutputSettings with { LimitToStartingMonitor = enabled };
        if (!starting)
        {
            foreach (var overlay in overlays) { if (SelectionMonitors.Contains(overlay.MonitorBounds)) overlay.Show(); else overlay.Hide(); }
            foreach (var monitor in SelectionMonitors.Where(m => overlays.All(o => o.MonitorBounds != m)))
            { var overlay = new CaptureOverlay(this, monitor); overlays.Add(overlay); overlay.Show(); }
            if (limit is Rectangle clip) cursorConfinement = confineCursor(clip);
            ReplaceGuide();
        }
        UpdateMenu(); RefreshOverlays();
    }

    internal Task ChangeCaptureSourceAsync(bool freeze, bool cursor) => RefreshSnapshotAsync(OutputSettings with { FreezeFrame = freeze, IncludeCursor = cursor });
    internal async Task ChangeMetadataAsync(Services.CaptureMetadataMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (completed || refreshing) return;
        if (mode != Services.CaptureMetadataMode.None && OutputSettings.MetadataMode == Services.CaptureMetadataMode.None)
            await RefreshSnapshotAsync(OutputSettings with { MetadataMode = mode });
        else { OutputSettings = OutputSettings with { MetadataMode = mode }; UpdateMenu(); }
    }
    private DesktopSnapshot TakeFrame(Services.AppSettings settings) => recapture is not null
        ? recapture(settings.IncludeCursor, options.Intent == CaptureIntent.Screenshot && Services.CaptureMetadata.RequiresPreparation(settings))
        : currentFrameFactory?.Invoke() ?? throw new InvalidOperationException("この選択画面では画像を再取得できません。");

    private async Task RefreshSnapshotAsync(Services.AppSettings next)
    {
        if (completed || refreshing) return;
        refreshing = true;
        DesktopSnapshot? replacement = null;
        try
        {
            snapTimer?.Stop();
            UI.CaptureUiPreparation.Prepare(overlays.Cast<Form?>().Append(guide), SelectionMenu.CloseForCapture);
            replacement = TakeFrame(next);
            if (replacement.Bounds != Snapshot.Bounds || !replacement.Monitors.OrderBy(m => m.X).ThenBy(m => m.Y).SequenceEqual(Snapshot.Monitors.OrderBy(m => m.X).ThenBy(m => m.Y)))
                throw new IOException("画面構成が変わりました。撮影を開始し直してください。");
            var nextWindows = readWindows?.Invoke(replacement.Bounds) ?? windows;
            if (replacement.Pointer is { } pointer && Services.CaptureMetadata.RequiresPreparation(next))
                replacement.Context ??= await Services.CaptureContextClient.Shared.ObserveAsync(new Point(pointer.Screen.X, pointer.Screen.Y));
            if (completed) return;
            var old = Snapshot; Snapshot = replacement; replacement = null;
            windows = nextWindows; gesture.UpdateTargets(SelectionMonitors, windows, selectionBounds);
            OutputSettings = next; old.Dispose(); SelectionError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        { SelectionError = "画面を取り直せませんでした: " + ex.Message; }
        finally
        {
            replacement?.Dispose();
            if (!completed)
            {
                menuOpen = menuTransition = false;
                // サブメニュー内で移動したポインタを新しい追従基準にし、固定枠を飛ばさない。
                RebaseFixedPointer();
                foreach (var overlay in overlays.Where(o => !o.IsDisposed && SelectionMonitors.Contains(o.MonitorBounds))) overlay.Show();
                var owner = overlays.FirstOrDefault(o => o.Visible && o.MonitorBounds.Contains(gesture.RawCurrent)) ?? overlays.FirstOrDefault(o => o.Visible);
                owner?.Activate(); owner?.Focus();
                UpdateMenu(); if (ShowSelectionGuide && owner is not null) { guide?.Show(owner); UpdateGuidePointer(); }
                RefreshOverlays(); snapTimer?.Start();
            }
            refreshing = false;
        }
    }

    internal void ApplyInputPreset(Services.SavedRegion preset)
    {
        if (completed) return;
        try
        {
            var displays = readPresetDisplays();
            if (!displays.Select(d => d.Bounds).OrderBy(b => b.X).ThenBy(b => b.Y)
                .SequenceEqual(Snapshot.Monitors.OrderBy(b => b.X).ThenBy(b => b.Y)))
                throw new InvalidOperationException("選択開始後に画面構成が変わりました。撮影を開始し直してください。");
            Rectangle bounds = Services.RegionCoordinates.Resolve(preset, displays);
            SelectionError = null;
            bool grid = Grid20;
            ReleasePointer();
            cursorConfinement?.Dispose(); cursorConfinement = null;
            Rectangle containing = Snapshot.Monitors.FirstOrDefault(m => m.Contains(bounds));
            selectionBounds = OutputSettings.LimitToStartingMonitor && !containing.IsEmpty ? containing : null;
            SelectionMonitors = selectionBounds is Rectangle limited ? [limited] : Snapshot.Monitors;
            OutputSettings = OutputSettings with { LimitToStartingMonitor = selectionBounds is not null };
            // 元のドラッグは右クリック時点で終了済み。プリセットは新しい固定枠として呼び出す。
            Mode = CaptureMode.FixedRegion; activeFixedSize = bounds.Size;
            gesture = CreateGesture(Mode, grid);
            if (!starting && selectionBounds is Rectangle clip) cursorConfinement = confineCursor(clip);
            Point pointer = starting ? lastPointerPosition ?? initialPointer : CaptureNative.CursorPosition();
            if (selectionBounds is Rectangle pointerLimit)
                pointer = new(Math.Clamp(pointer.X, pointerLimit.Left, pointerLimit.Right - 1), Math.Clamp(pointer.Y, pointerLimit.Top, pointerLimit.Bottom - 1));
            gesture.PlaceFixed(bounds, pointer);
            lastPointerPosition = pointer;
            lastSnap = lastHeightLock = lastWidthLock = false;
            if (!starting)
            {
                foreach (Rectangle monitor in SelectionMonitors.Where(m => overlays.All(o => o.MonitorBounds != m)))
                { var overlay = new CaptureOverlay(this, monitor); overlays.Add(overlay); overlay.Show(); }
                ReplaceGuide();
            }
            SelectionMenu.SetMode(Mode); UpdateMenu(); RefreshOverlays();
        }
        catch (Exception ex)
        {
            SelectionError = "入力プリセットを呼び出せません: " + ex.Message;
            RefreshOverlays();
        }
    }

    internal void ChangeAspectRatio(CaptureAspectRatio ratio)
    {
        if (!Enum.IsDefined(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio));
        AspectRatio = ratio; gesture.AspectRatio = ratio;
        SelectionMenu.SetRatio(ratio);
        RefreshOverlays();
    }

    internal void ChangePadding(int pixels)
    {
        if (pixels is < 0 or > SSCapture.Services.ScreenshotPadding.MaximumPixels) throw new ArgumentOutOfRangeException(nameof(pixels));
        OutputSettings = OutputSettings with { ScreenshotPaddingPixels = pixels };
        SelectionMenu.SetPadding(pixels);
    }

    internal void ApplyPreset(SSCapture.Services.CapturePreset preset)
    {
        OutputSettings = preset.Apply(OutputSettings);
        Destination = OutputSettings.DefaultDestination;
        SelectionMenu.SetDestination(Destination);
        SelectionMenu.SetPadding(OutputSettings.ScreenshotPaddingPixels);
        SelectionMenu.SetApplication(options.Intent == CaptureIntent.Screenshot ? OutputSettings.SelectedImageEditorId : OutputSettings.SelectedVideoEditorId);
        UpdateMenu();
    }

    internal void ChangeApplication(string id)
    {
        var apps = options.Intent == CaptureIntent.Screenshot ? OutputSettings.ImageEditors : OutputSettings.VideoEditors;
        SSCapture.Services.ExternalApplication.ValidateReference(apps, id);
        OutputSettings = options.Intent == CaptureIntent.Screenshot ? OutputSettings with { SelectedImageEditorId = id } : OutputSettings with { SelectedVideoEditorId = id };
        SelectionMenu.SetApplication(id);
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
            SelectionMenu.CloseForCapture();
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
        guide = new UI.SelectionGuideForm(owner.MonitorBounds, options.Intent, () => { if (!refreshing) Cancel(); },
            Mode == CaptureMode.FixedRegion ? activeFixedSize : null, HandleCommandKey, activeAnchor, AltKeyChanged, Mode,
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

    internal void Move(bool queuedKeyState = false, Point? eventPosition = null)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
        try
        {
            Keys modifiers = ReadModifiers(queuedKeyState);
            Point position = eventPosition ?? CaptureNative.CursorPosition();
            bool crossing = (modifiers & (Keys.Shift | Keys.Control)) == (Keys.Shift | Keys.Control);
            UpdateMonitorRestriction(position, crossing);
            // キュー内のDown/Upを現在位置で読み替えると、高速ドラッグがクリックに化ける。
            MoveTo(position, (modifiers & Keys.Alt) != 0,
                !crossing && (modifiers & Keys.Shift) != 0, !crossing && (modifiers & Keys.Control) != 0);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void MoveTo(Point physicalPosition, bool snap = false, bool lockHeight = false, bool lockWidth = false)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
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

    internal void UpdateMonitorRestriction(Point pointer, bool crossing)
    {
        if (completed || refreshing || selectionBounds is not Rectangle bounds) return;
        if (crossing)
        {
            cursorConfinement?.Dispose(); cursorConfinement = null;
            if (!bounds.Contains(pointer) && Snapshot.Monitors.Any(m => m.Contains(pointer)))
            {
                selectionBounds = null;
                SelectionMonitors = Snapshot.Monitors;
                gesture.ExpandMonitors(SelectionMonitors);
                OutputSettings = OutputSettings with { LimitToStartingMonitor = false }; UpdateMenu();
                if (!starting)
                    foreach (Rectangle monitor in SelectionMonitors.Where(m => overlays.All(o => o.MonitorBounds != m)))
                    { var overlay = new CaptureOverlay(this, monitor); overlays.Add(overlay); overlay.Show(); }
            }
        }
        else if (!starting && cursorConfinement is null) cursorConfinement = confineCursor(bounds);
    }

    internal void ModifiersChanged() => UpdateModifiers(false);
    internal void AltKeyChanged() => UpdateModifiers(true);

    private void UpdateModifiers(bool queuedKeyState)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
        try
        {
            if (Control.MouseButtons == MouseButtons.None) suppressedButtons = MouseButtons.None;
            Keys modifiers = ReadModifiers(queuedKeyState);
            bool snap = (modifiers & Keys.Alt) != 0, height = (modifiers & Keys.Shift) != 0, width = (modifiers & Keys.Control) != 0;
            bool crossing = height && width;
            Point currentPointer = CaptureNative.CursorPosition();
            UpdateMonitorRestriction(currentPointer, crossing);
            if (crossing) height = width = false;
            // キーだけでなくポインタもポーリング。案内上ではoverlayへMouseMoveが来ない。
            if (currentPointer != lastPointerPosition) MoveTo(currentPointer, snap, height, width);
            if ((snap != lastSnap || height != lastHeightLock || width != lastWidthLock) && lastPointerPosition is Point point)
                MoveTo(point, snap, height, width);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal bool HandleCommandKey(Keys keyData) => HandleCommandKey(keyData, false);

    internal bool HandleCommandKey(Keys keyData, bool repeated)
    {
        if (refreshing) return true;
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
            if (handled) { activeAnchor = gesture.FixedAnchor; guide?.UpdateAnchor(activeAnchor); UpdateMenu(); RefreshOverlays(); }
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

    internal void MouseDown(CaptureOverlay overlay, MouseButtons button, Point? eventPosition = null)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
        if (suppressedButtons != MouseButtons.None) return;
        if (button == MouseButtons.Right)
        {
            if (gesture.IsPressed) { AbortSelection(); suppressedButtons |= MouseButtons.Right; overlay.Capture = false; }
            else ShowMenu(overlay);
            return;
        }
        if (button is not (MouseButtons.Left or MouseButtons.Middle) || pressedButton != MouseButtons.None) return;
        Move(true, eventPosition);
        if (completed) return;
        dragOwner = overlay;
        pressedButton = button;
        if (button == MouseButtons.Left) gesture.Begin(gesture.RawCurrent, CaptureNative.DragThreshold(overlay.DeviceDpi), lastSnap, lastHeightLock, lastWidthLock);
        overlay.Capture = true;
        if (!overlay.Capture) Cancel();
    }

    internal void MouseUp(MouseButtons button, Point? eventPosition = null)
    {
        if ((suppressedButtons & button) != 0) { suppressedButtons &= ~button; return; }
        if (completed || refreshing || menuOpen || menuTransition || button != pressedButton || pressedButton == MouseButtons.None) return;
        Move(true, eventPosition);
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
        if (completed || refreshing || starting) return;
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
            RebaseFixedPointer();
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
        if (!starting && !refreshing && !menuOpen && !menuTransition) Cancel();
    }

    private void RebaseFixedPointer()
    {
        if (starting || Mode != CaptureMode.FixedRegion) return;
        Point pointer = CaptureNative.CursorPosition();
        if (selectionBounds is Rectangle limit)
            pointer = new(Math.Clamp(pointer.X, limit.Left, limit.Right - 1), Math.Clamp(pointer.Y, limit.Top, limit.Bottom - 1));
        gesture.PlaceFixed(gesture.Selection, pointer); lastPointerPosition = pointer;
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

    internal async void CompleteSelection(CaptureSelection selection, bool entireMonitor = false)
    {
        if (completed || refreshing) return;
        if (selectionBounds is Rectangle bounds && (!bounds.Contains(selection.Bounds)
            || selection.Polygon?.Any(point => !bounds.Contains(point)) == true)) return;
        Size fixedSize = activeFixedSize;
        if (Mode == CaptureMode.FixedRegion && !entireMonitor && (selection.Polygon is not null
            || selection.Bounds.Size != fixedSize || !CaptureGeometry.IsFullyCovered(selection.Bounds, SelectionMonitors))) return;
        completed = true;
        Bitmap? result = null;
        Services.CaptureMetadata? metadata = null;
        try
        {
            CloseOverlays();
            if (options.Intent == CaptureIntent.Screenshot || options.IncludeRecordingImage)
            {
                using DesktopSnapshot? current = OutputSettings.FreezeFrame || options.Intent == CaptureIntent.Recording ? null : TakeFrame(OutputSettings);
                DesktopSnapshot frame = current ?? Snapshot;
                if (frame.Pointer is { } pointer && OutputSettings.MetadataMode != Services.CaptureMetadataMode.None && Destination.HasFlag(OutputDestination.File))
                {
                    var context = frame.Context ?? await Services.CaptureContextClient.Shared.ObserveAsync(new Point(pointer.Screen.X, pointer.Screen.Y));
                    if (current is not null) context = context with { FocusAtSelectionStart = Snapshot.Context?.FocusedElement };
                    metadata = Services.CaptureMetadata.Create(selection.Bounds, OutputSettings.ScreenshotPaddingPixels, frame.CapturedAt, pointer, context,
                        Mode == CaptureMode.Window && !entireMonitor && context.PointerApplication.Bounds == Services.PixelBounds.From(selection.Bounds) ? context.PointerApplication : null,
                        selection.Polygon);
                }
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
            completion.TrySetResult(new CaptureOutcome(result, selection, outputSettings: OutputSettings with { DefaultDestination = Destination }, metadata: metadata));
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
