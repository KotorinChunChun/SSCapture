namespace SSCapture.Capture;

internal static class CaptureSession
{
    /// <summary>Call on the application's STA UI thread. The caller owns the outcome.</summary>
    public static async Task<CaptureOutcome> SelectAsync(CaptureOptions options, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null)
    {
        ValidateCall(options);
        return await SelectWithDelayAsync(options, current => SelectOnceAsync(current, gridChanged, guideChanged));
    }

    internal static async Task<CaptureOutcome> SelectWithDelayAsync(CaptureOptions options,
        Func<CaptureOptions, Task<CaptureOutcome>> select, Func<TimeSpan, Task>? wait = null)
    {
        var delay = options.AllowStartDelay ? options.InitialInputPreset?.StartDelay ?? CaptureStartDelay.None : CaptureStartDelay.None;
        while (true)
        {
            if (delay != CaptureStartDelay.None)
            {
                await CaptureDelay.WaitAsync(delay, wait);
                options = options with { StartWasDelayed = true };
            }
            CaptureOutcome outcome = await select(options);
            if (outcome.DelayedRestart is not { } restart) return outcome;
            outcome.Dispose();
            options = restart.Options;
            delay = restart.Delay;
        }
    }

    private static async Task<CaptureOutcome> SelectOnceAsync(CaptureOptions options, Action<bool>? gridChanged, Action<bool>? guideChanged)
    {
        Point initialPointer = CaptureNative.CursorPosition();
        // Freeze the current desktop before showing the selection overlays.
        bool metadata = !options.SelectionOnly && options.Intent == CaptureIntent.Screenshot && options.OutputSettings is { } settings && Services.CaptureMetadata.RequiresPreparation(settings);
        DesktopSnapshot snapshot = DesktopSnapshot.Take(options.IncludeCursor, metadata);
        if (metadata)
            snapshot.Context = await Services.CaptureContextClient.Shared.ObserveAsync(new Point(snapshot.Pointer!.Screen.X, snapshot.Pointer.Screen.Y));
        IReadOnlyList<CaptureWindow> windows;
        try { windows = CaptureNative.CaptureWindows(snapshot.Bounds, includeClientBounds: options.Intent == CaptureIntent.Screenshot); }
        catch { snapshot.Dispose(); throw; }
        return await SelectAsync(options, snapshot, windows.Select(window => window.Bounds).ToArray(),
            () => RetakeDesktop(options.IncludeCursor, metadata), initialPointer, gridChanged, guideChanged, RetakeDesktop, bounds =>
        {
            windows = CaptureNative.CaptureWindows(bounds, includeClientBounds: options.Intent == CaptureIntent.Screenshot);
            return windows.Select(window => window.Bounds).ToArray();
        }, captureWindowsKey: true, findWindow: point =>
        {
            for (int i = 0; i < windows.Count; i++)
                if (windows[i].Bounds.Contains(point)) return windows[i];
            return null;
        }, findWindowGroup: root => CaptureWindowGroups.ForegroundGroup(root, windows),
            readGuideDisplays: () => InstantCapture.ReadDisplays().Select(display => display with
            {
                MonitorName = Services.RegionCoordinates.ReadMonitorName(display.DeviceName)
            }).ToArray());
    }

    /// <summary>Consumes snapshot. Never reads desktop pixels or enumerates real windows.</summary>
    internal static Task<CaptureOutcome> SelectAsync(CaptureOptions options, DesktopSnapshot snapshot,
        IReadOnlyList<Rectangle>? windows = null, Func<DesktopSnapshot>? currentFrameFactory = null, Point? initialPointer = null, Action<bool>? gridChanged = null, Action<bool>? guideChanged = null,
        Func<bool, bool, DesktopSnapshot>? recapture = null, Func<Rectangle, IReadOnlyList<Rectangle>>? readWindows = null,
        Func<Point, string?>? readTitle = null, bool captureWindowsKey = false, Func<Point, CaptureWindow?>? findWindow = null,
        Func<CaptureWindow, IReadOnlyList<CaptureWindow>>? findWindowGroup = null,
        Func<IReadOnlyList<DisplayTarget>>? readGuideDisplays = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            ValidateCall(options);
            if (!options.FreezeFrame && options.Intent == CaptureIntent.Screenshot && currentFrameFactory is null && recapture is null)
                throw new ArgumentException("画面固定OFFの合成画像検証には画像取得関数を指定してください。", nameof(currentFrameFactory));
            SessionController session = new(options, snapshot, windows ?? [], currentFrameFactory,
                initialPointer ?? snapshot.Monitors.FirstOrDefault().Location, gridChanged: gridChanged, guideChanged: guideChanged, recapture: recapture, readWindows: readWindows,
                readTitle: readTitle, captureWindowsKey: captureWindowsKey, findWindow: findWindow, findWindowGroup: findWindowGroup,
                readGuideDisplays: readGuideDisplays);
            if (options.InitialInputPreset is { UseFixedCoordinates: true } preset && options.RestartState?.UseFixedCoordinates != false)
                _ = session.ApplyInputPresetAsync(preset, restorePositionOnly: options.RestartState is not null);
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

    private static DesktopSnapshot RetakeDesktop(bool includeCursor, bool observeContext)
    {
        CaptureNative.SynchronizeDesktop();
        return DesktopSnapshot.Take(includeCursor, observeContext);
    }

    internal static bool SameMonitors(IEnumerable<Rectangle> left, IEnumerable<Rectangle> right) =>
        left.OrderBy(m => m.X).ThenBy(m => m.Y).SequenceEqual(right.OrderBy(m => m.X).ThenBy(m => m.Y));

    private static void ValidateCall(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("撮影は STA UI スレッドで開始してください。");
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Intent) || !Enum.IsDefined(options.FixedAnchor)
            || (options.Destination & ~(OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp | OutputDestination.Pinned)) != 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Mode == CaptureMode.FixedRegion) CaptureGeometry.ValidateFixedSize(options.FixedSize);
    }
}

internal sealed partial class SessionController
{
    private readonly TaskCompletionSource<CaptureOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CaptureOverlay> overlays = [];
    private readonly CaptureOptions options;
    private readonly Func<Rectangle, Services.AppSettings, Task<(Bitmap Image, Services.CaptureMetadata? Metadata)?>>? scrollCollector;
    private bool IsRectangleMode => Mode is CaptureMode.Rectangle or CaptureMode.Scroll;
    private bool IsScreenshot => options.Intent == CaptureIntent.Screenshot;
    internal SSCapture.Services.AppSettings OutputSettings { get; private set; }
    private readonly Func<DesktopSnapshot>? currentFrameFactory;
    private readonly Func<bool, bool, DesktopSnapshot>? recapture;
    private readonly Func<Rectangle, IReadOnlyList<Rectangle>>? readWindows;
    private readonly Func<Point, string?>? readTitle;
    private readonly Func<Point, CaptureWindow?>? findWindow;
    private readonly Func<CaptureWindow, IReadOnlyList<CaptureWindow>> findWindowGroup;
    private bool exclusiveWindow;
    private WindowCapturePreview? windowPreview;
    private bool windowPreviewIsControl;
    private readonly HashSet<CaptureOverlay> auxiliaryOverlays = [];
    private CaptureTargetDrag? targetDrag;
    private readonly bool captureWindowsKey;
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
    private bool hasSelectionInput;
    private bool completed;
    private bool starting = true;
    private bool menuTransition;
    private bool menuOpen;
    private bool menuClosedByCommand;
    private int menuGeneration;
    private Point? lastPointerPosition;
    private bool lastSnap;
    private bool lastHeightLock, lastWidthLock;
    private CaptureKeyAction lastRectangleAction;
    private Keys ignoredModifiers;
    private Size activeFixedSize;
    private readonly Func<IReadOnlyList<Services.DisplayLayout>> readPresetDisplays;
    private Services.SavedRegion selectedInput;
    private Services.CapturePreset selectedOutput;
    private Rectangle? selectedInputBounds;
    private MouseButtons suppressedButtons;
    private System.Windows.Forms.Timer? snapTimer;
    private bool overlayUpdatePending;
    private CaptureWindowsKey? windowsKey;
    private nint captureStartForeground;
    private bool idleMonitorMove;
    private CaptureModifiers activeModifiers;
    private bool activeDrag;
    private (Point Pointer, long Time)? altSnapPress;
    private bool altSnapEnabled;
    private CaptureMode? modeBeforeWindowShortcut;
    internal string? ModifierHint { get; private set; }
    internal bool HasPointerPress => pressedButton != MouseButtons.None;
    private bool desktopHeld, desktopPress;
    private bool SelectingDesktop => desktopPress || !HasPointerPress && desktopHeld;
    private bool FreeDesktopDrag => desktopPress && IsScreenshot;
    internal bool IsSecondaryPress(MouseButtons button) => HasPointerPress && button != pressedButton;
    private bool HasWindowDrag => HasPointerPress && !desktopPress && HighlightWindow && gesture.HasDragged && targetDrag is not null;
    internal IReadOnlyList<Point> TargetDragPath => controlDrag?.Path ?? (HasWindowDrag ? targetDrag!.Path : []);
    internal Rectangle TargetDragBounds => controlDrag?.TrailBounds ?? (HasWindowDrag ? targetDrag!.TrailBounds : Rectangle.Empty);
    internal IReadOnlyList<Rectangle> TargetDragHighlights => !DimCaptureBackground ? [] : controlDrag?.Highlights ?? (HasWindowDrag ? targetDrag!.BrightWindows : []);
    internal bool PointerLabelsSuppressed => menuOpen || menuTransition;
    internal Point Pointer => gesture.RawCurrent;
    internal bool HighlightWindow => Mode == CaptureMode.Window;
    private bool UsesWindowSource => HighlightWindow && exclusiveWindow;
    private bool clientAreaOnly;
    private bool UsesClientWindowArea => HighlightWindow && clientAreaOnly;
    internal WindowCaptureFrame? PreviewFrame => windowPreview?.Frame is { IsCompletedSuccessfully: true } frame ? frame.Result : null;
    internal Rectangle HighlightedWindow => FreeDesktopDrag ? Snapshot.Monitors.FirstOrDefault(monitor => monitor.Contains(Pointer))
        : SelectingDesktop ? gesture.MonitorAt(Pointer)
        : HasWindowDrag ? Rectangle.Empty : UsesWindowSource ? PreviewFrame?.Bounds ?? Rectangle.Empty
        : UsesClientWindowArea ? VisibleClientBounds()
        : HighlightWindow ? gesture.WindowAt(Pointer) : TwoPointSelection || Mode == CaptureMode.Scroll ? Rectangle.Empty : gesture.PressedWindow;
    internal Rectangle HighlightForOverlay(Rectangle monitor) => SelectingControl ? HasControlDrag ? Rectangle.Empty : ControlBounds : MarginSelection is Rectangle adjusted ? adjusted : FreeDesktopDrag
        ? targetDrag?.Monitors.Contains(monitor) == true ? monitor : Rectangle.Empty : HighlightedWindow;
    internal Rectangle DisplaySelection => SelectingControl ? HasControlDrag ? Rectangle.Empty : ControlBounds
        : UsesClientWindowArea && !SelectingDesktop ? HighlightedWindow : MarginSelection ??
            (SelectingDesktop || HasWindowDrag ? Rectangle.Empty : PreviewFrame?.Bounds ?? Selection);

    internal CaptureMode Mode { get; private set; }
    internal bool Grid20 => gesture.Grid20;
    internal CaptureAnchor FixedAnchor => gesture.FixedAnchor;
    internal CaptureAspectRatio AspectRatio { get; private set; }
    internal DesktopSnapshot Snapshot { get; private set; }
    internal Task<CaptureOutcome> Completion => completion.Task;
    internal bool Dragging => gesture.IsPressed || HasControlDrag;
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
        Func<bool, bool, DesktopSnapshot>? recapture = null, Func<Rectangle, IReadOnlyList<Rectangle>>? readWindows = null,
        Func<Point, string?>? readTitle = null, bool captureWindowsKey = false, Func<Point, CaptureWindow?>? findWindow = null,
        Func<CaptureWindow, IReadOnlyList<CaptureWindow>>? findWindowGroup = null,
        Func<IReadOnlyList<DisplayTarget>>? readGuideDisplays = null,
        Func<Rectangle, Services.AppSettings, Task<(Bitmap Image, Services.CaptureMetadata? Metadata)?>>? scrollCollector = null)
    {
        this.options = options;
        this.scrollCollector = scrollCollector;
        this.recapture = recapture; this.readWindows = readWindows;
        this.readTitle = readTitle; this.captureWindowsKey = captureWindowsKey; this.findWindow = findWindow;
        this.findWindowGroup = findWindowGroup ?? (root => [root]);
        this.readGuideDisplays = readGuideDisplays;
        activeAnchor = options.FixedAnchor;
        if (options.RestartState is { } restart)
        {
            AspectRatio = restart.AspectRatio;
            exclusiveWindow = restart.ExclusiveWindow;
            clientAreaOnly = restart.ClientAreaOnly;
            temporaryPadding = restart.TemporaryPadding;
            hasSelectionInput = true;
        }
        activeFixedSize = options.FixedSize ?? Services.PresetSettings.InitialFixedSize;
        this.readPresetDisplays = readPresetDisplays ?? Services.RegionCoordinates.ReadDisplays;
        Mode = options.Mode == CaptureMode.Scroll && !options.AllowScroll ? CaptureMode.Rectangle : options.Mode;
        this.gridChanged = gridChanged;
        this.guideChanged = guideChanged;
        ShowSelectionGuide = options.ShowSelectionGuide;
        this.currentFrameFactory = currentFrameFactory;
        this.initialPointer = initialPointer ?? snapshot.Monitors[0].Location;
        this.confineCursor = confineCursor ?? CursorConfinement.Acquire;
        Snapshot = snapshot;
        Destination = AllowedDestination(options.Destination);
        OutputSettings = (options.OutputSettings ?? new SSCapture.Services.AppSettings()) with
        { FreezeFrame = options.FreezeFrame, IncludeCursor = options.IncludeCursor, DimCaptureBackground = options.DimCaptureBackground,
            LimitToStartingMonitor = options.LimitToStartingMonitor };
        OutputSettings = OutputSettings with
        {
            CaptureBeforeKeys = OutputSettings.CaptureBeforeKeys.WithBeforeActivations(OutputSettings.ControlActivation, OutputSettings.WindowActivation)
        };
        if (options.LimitToStartingMonitor && options.Mode != CaptureMode.FullScreen)
        {
            selectionBounds = snapshot.Monitors.FirstOrDefault(monitor => monitor.Contains(this.initialPointer));
            if (selectionBounds.Value.IsEmpty) throw new InvalidOperationException("開始位置のモニターが見つかりません。もう一度選択してください。");
            SelectionMonitors = [selectionBounds.Value];
        }
        else SelectionMonitors = snapshot.Monitors;
        this.windows = windows.ToArray();
        gesture = CreateGesture(Mode, options.Grid20);
        selectedInput = options.InitialInputPreset ?? OutputSettings.SavedRegions.FirstOrDefault(p => p.IsDefault) ?? Services.SavedRegion.FromSettings("撮影開始時", OutputSettings with { DefaultMode = Mode, FixedWidth = activeFixedSize.Width, FixedHeight = activeFixedSize.Height, FixedCaptureAnchor = activeAnchor });
        selectedOutput = options.InitialOutputPreset ?? OutputSettings.CapturePresets.FirstOrDefault(p => p.IsDefault) ?? Services.CapturePreset.FromSettings("撮影開始時", OutputSettings with { DefaultDestination = Destination });
        SelectionMenu = CreateSelectionMenu();
        UpdateMenu();
    }

    private CaptureContextMenu CreateSelectionMenu()
    {
        var menu = new CaptureContextMenu(Destination, ChangeDestination,
            () => FinishCommand(true), Cancel, Mode, options.Grid20, ChangeMode, ChangeGrid20, () => CaptureDesktop(menuPointer),
            showGuide: ShowSelectionGuide, changeGuide: ChangeGuideVisibility, changeRatio: ChangeAspectRatio,
            defaultPadding: OutputSettings.ScreenshotPaddingPixels, changePadding: IsScreenshot ? ChangePadding : null,
            presets: OutputSettings.CapturePresets, applyPreset: ApplyPreset,
            applications: EditorApplications, selectedApplication: SelectedEditorId,
            changeApplication: ChangeApplication, inputPresets: OutputSettings.SavedRegions, applyInputPreset: ApplyInputPreset,
            editing: new(OutputSettings, activeFixedSize, activeAnchor, selectionBounds is not null, IsScreenshot,
                recapture is not null || currentFrameFactory is not null, ChangeFixedSize, ChangeFixedAnchor, ChangeMonitorLimit, ChangeColor,
                mode => _ = ChangeMetadataAsync(mode), ChangeDim, enabled => _ = ChangeCaptureSourceAsync(enabled, OutputSettings.IncludeCursor),
                enabled => _ = ChangeCaptureSourceAsync(OutputSettings.FreezeFrame, enabled), ChangeSnapPrecision, ChangeOutputDetails,
                ChangeOpenFolder, () => _ = ResetInputPresetAsync(), ResetOutputPreset,
                enabled => ApplyMenuSetting(OutputSettings with { ShowPointerGuide = enabled }),
                enabled => ApplyMenuSetting(OutputSettings with { RecordSystemAudio = enabled }),
                enabled => ApplyMenuSetting(OutputSettings with { RecordMicrophone = enabled })),
            changeWindowSource: IsScreenshot && findWindow is not null
                ? enabled => { exclusiveWindow = enabled; UpdateMenu(); RefreshOverlays(); } : null,
            openSettingsLink: OpenCaptureSettings, allowScroll: options.AllowScroll,
            restartAfterDelay: options.AllowStartDelay ? RestartAfterDelay : null);
        menu.AddRectangleSelectionMethods(
            () => ChangeSelectionMethod(CaptureSelectionMethod.Drag), () => ChangeSelectionMethod(CaptureSelectionMethod.TwoClicks));
        menu.AddPresentationOptions(OutputSettings.ShowMagnifier, ChangeMagnifierVisibility,
            OutputSettings.ShareCapturePresentation, ChangeSharePresentation);
        if (IsScreenshot && findWindow is not null)
            menu.AddWindowAreaOptions(enabled =>
            {
                clientAreaOnly = enabled;
                ResetTargetMargin();
                UpdateMenu();
                RefreshOverlays();
            });
        if (IsScreenshot && !options.SelectionOnly)
            menu.AddWindowCropOptions(() => ApplyMenuSetting(OutputSettings with { WindowCrop = selectedOutput.WindowCrop }),
                () => ApplyMenuSetting(OutputSettings with { WindowCrop = default }));
        if (options.SelectionOnly)
        {
            foreach (var item in menu.Items.Cast<ToolStripItem>())
                if (item.Name is "CapturePresetMenu" or "CaptureOutputGroup" or "OpenSettings" or "CaptureSettingsMenu") item.Available = false;
        }
        menu.SetRatio(AspectRatio);
        menu.Closed += MenuClosed;
        // WinForms can label a parent close as AppFocusChange when a child item was clicked.
        menu.CommandInvoking += () => { if (menuTransition && !menuOpen) menuClosedByCommand = true; };
        return menu;
    }

    private SelectionGesture CreateGesture(CaptureMode mode, bool grid20)
    {
        var next = new SelectionGesture(mode == CaptureMode.Scroll ? CaptureMode.Rectangle : mode, SelectionMonitors, windows, activeFixedSize, activeAnchor) { Grid20 = grid20, AspectRatio = AspectRatio, WindowLimit = selectionBounds };
        next.SetSnapPrecision(OutputSettings.SnapDistancePixels, OutputSettings.GridStepPixels);
        return next;
    }
    internal void ChangeSnapPrecision(int distance, int step)
    {
        gesture.SetSnapPrecision(distance, step);
        OutputSettings = OutputSettings with { SnapDistancePixels = distance, GridStepPixels = step };
        UpdateMenu(); RefreshOverlays();
    }

    private int? temporaryPadding;
    private int EffectivePadding(Services.CapturePreset output, Services.SavedRegion input) =>
        temporaryPadding ?? output.ResolvePadding(input.PaddingPixels);

    internal bool InputPresetModified => Mode != (selectedInput.UseFixedCoordinates ? CaptureMode.FixedRegion : selectedInput.Mode)
        || activeFixedSize != selectedInput.Bounds.Size || activeAnchor != selectedInput.FixedAnchor
        || OutputSettings.FreezeFrame != selectedInput.FreezeFrame || OutputSettings.IncludeCursor != selectedInput.IncludeCursor
        || OutputSettings.SelectionMethod != selectedInput.SelectionMethod
        || AspectRatio != CaptureAspectRatio.Free
        || OutputSettings.RecordSystemAudio != selectedInput.RecordSystemAudio || OutputSettings.RecordMicrophone != selectedInput.RecordMicrophone
        || OutputSettings.MicrophoneDeviceId != selectedInput.MicrophoneDeviceId
        || selectedInputBounds is Rectangle bounds && Selection != bounds;
    internal bool OutputPresetModified => temporaryPadding.HasValue || Destination != selectedOutput.Destination || OutputSettings.SaveDirectory != selectedOutput.SaveDirectory
        || OutputSettings.FileNamePattern != selectedOutput.FileNamePattern || OutputSettings.OcrSeparator != selectedOutput.OcrSeparator
        || OutputSettings.ScreenshotColorDepth != selectedOutput.ColorDepth || OutputSettings.OpenFolderAfterSave != selectedOutput.OpenFolderAfterSave
        || OutputSettings.WindowCrop != selectedOutput.WindowCrop || OutputSettings.MetadataMode != selectedOutput.MetadataMode
        || OutputSettings.SelectedImageEditorId != (selectedOutput.ImageEditorId ?? "")
        || OutputSettings.SelectedVideoEditorId != (selectedOutput.VideoEditorId ?? "");
    private void ApplyMenuSetting(Services.AppSettings settings)
    {
        OutputSettings = settings;
        UpdateMenu();
        RefreshOverlays();
    }

    private void OpenCaptureSettings(CaptureSettingsDestination destination, string? shortcutControlName)
    {
        string? presetId = destination switch
        {
            CaptureSettingsDestination.InputPreset => selectedInput.Id,
            CaptureSettingsDestination.OutputPreset => selectedOutput.Id,
            _ => null
        };
        FinishCommand(true, settingsLink: new(destination, presetId, shortcutControlName));
    }

    private void UpdateMenu()
    {
        SelectionMenu.SetWindowSource(Mode, exclusiveWindow);
        SelectionMenu.UpdateEditing(OutputSettings, activeFixedSize, activeAnchor, selectionBounds is not null);
        SelectionMenu.SetPresetStatus(selectedInput.Name, selectedOutput.Name, InputPresetModified, OutputPresetModified, selectedInput.Id, selectedOutput.Id);
        SelectionMenu.SetRectangleSelectionMethod(OutputSettings.SelectionMethod == CaptureSelectionMethod.TwoClicks);
        SelectionMenu.SetPresentationOptions(OutputSettings.ShowMagnifier, OutputSettings.ShareCapturePresentation);
        SelectionMenu.SetWindowCrop(OutputSettings.WindowCrop, selectedOutput.WindowCrop);
        SelectionMenu.SetWindowArea(Mode, clientAreaOnly);
        UpdateControlShortcutMenu();
    }
    // 録画はピン留めへ出力できない。
    private OutputDestination AllowedDestination(OutputDestination destination) =>
        options.Intent == CaptureIntent.Recording ? destination & ~OutputDestination.Pinned : destination;
    private Services.ExternalApplication[] EditorApplications =>
        IsScreenshot ? OutputSettings.ImageEditors : OutputSettings.VideoEditors;
    private string SelectedEditorId => IsScreenshot ? OutputSettings.SelectedImageEditorId : OutputSettings.SelectedVideoEditorId;
    private void ChangeDestination(OutputDestination destination) { Destination = AllowedDestination(destination); SelectionMenu.SetDestination(Destination); UpdateMenu(); }
    internal Task ResetInputPresetAsync() => ApplyInputPresetAsync(selectedInput);
    internal void ResetOutputPreset()
    {
        temporaryPadding = null;
        ApplyPreset(selectedOutput);
    }
    internal void ChangeOpenFolder(bool enabled) { OutputSettings = OutputSettings with { OpenFolderAfterSave = enabled }; UpdateMenu(); }
    internal void ChangeOutputDetails(string directory, string pattern, string separator)
    {
        var next = OutputSettings with { SaveDirectory = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), FileNamePattern = pattern, OcrSeparator = separator };
        Services.CapturePreset.FromSettings("今回", next with { DefaultDestination = Destination }).Validate();
        OutputSettings = next; UpdateMenu(); SelectionMenu.CloseForCapture();
    }
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
            if (monitor.IsEmpty) throw new InvalidOperationException("ポインタのあるモニターが見つかりません。");
            limit = monitor;
        }
        idleMonitorMove = desktopHeld = false;
        cursorConfinement?.Dispose(); cursorConfinement = null;
        selectionBounds = limit; SelectionMonitors = limit is Rectangle limited ? [limited] : Snapshot.Monitors;
        gesture.UpdateTargets(SelectionMonitors, windows, limit);
        OutputSettings = OutputSettings with { LimitToStartingMonitor = enabled };
        if (!starting)
        {
            ShowSelectionOverlays();
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
        ? recapture(settings.IncludeCursor, IsScreenshot && Services.CaptureMetadata.RequiresPreparation(settings))
        : currentFrameFactory?.Invoke() ?? throw new InvalidOperationException("この選択画面では画像を再取得できません。");

    private async Task RefreshSnapshotAsync(Services.AppSettings next)
    {
        if (completed || refreshing) return;
        bool resumeAfterMenu = menuOpen || menuTransition;
        refreshing = true;
        DesktopSnapshot? replacement = null;
        try
        {
            snapTimer?.Stop();
            UI.CaptureUiPreparation.Prepare(overlays.Cast<Form?>().Append(guide), SelectionMenu.CloseForCapture);
            replacement = TakeFrame(next);
            if (replacement.Bounds != Snapshot.Bounds || !CaptureSession.SameMonitors(replacement.Monitors, Snapshot.Monitors))
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
                foreach (var overlay in overlays.Where(o => !o.IsDisposed && SelectionMonitors.Contains(o.MonitorBounds))) overlay.Show();
                var owner = overlays.FirstOrDefault(o => o.Visible && o.MonitorBounds.Contains(gesture.RawCurrent)) ?? overlays.FirstOrDefault(o => o.Visible);
                owner?.Activate(); owner?.Focus();
                UpdateMenu(); if (ShowSelectionGuide && owner is not null) { guide?.Show(owner); UpdateGuidePointer(); }
                refreshing = false;
                if (resumeAfterMenu) ResumePointerAfterMenu();
                RefreshOverlays(); snapTimer?.Start();
            }
            refreshing = false;
        }
    }

    internal void ApplyInputPreset(Services.SavedRegion preset) => _ = ApplyInputPresetAsync(preset);
    internal async Task ApplyInputPresetAsync(Services.SavedRegion preset, bool restorePositionOnly = false)
    {
        if (completed || refreshing || preset.Mode == CaptureMode.Scroll && !options.AllowScroll) return;
        try
        {
            preset.Validate();
            if (!preset.UseFixedCoordinates)
            {
                if (!await ApplyInputPresetSettingsAsync(preset)) return;
                activeFixedSize = preset.Bounds.Size; activeAnchor = preset.FixedAnchor;
                selectedInput = preset; selectedInputBounds = null; AspectRatio = CaptureAspectRatio.Free;
                Mode = preset.Mode; waitingForSecondPoint = false; ReleasePointer(); gesture = CreateGesture(Mode, Grid20);
                gesture.Move(lastPointerPosition ?? initialPointer); SelectionError = null;
                SelectionMenu.SetMode(Mode); SelectionMenu.SetPadding(OutputSettings.ScreenshotPaddingPixels); UpdateMenu(); ReplaceGuide(); RefreshOverlays();
                if (Mode == CaptureMode.FullScreen) CompleteSelection(new(selectionBounds ?? Snapshot.Bounds, null, Destination));
                return;
            }
            var displays = readPresetDisplays();
            if (!CaptureSession.SameMonitors(displays.Select(d => d.Bounds), Snapshot.Monitors))
                throw new InvalidOperationException("選択開始後に画面構成が変わりました。撮影を開始し直してください。");
            Rectangle bounds = Services.RegionCoordinates.Resolve(preset, displays);
            if (preset.ProfileVersion >= 1 && !restorePositionOnly)
            {
                if (!await ApplyInputPresetSettingsAsync(preset)) return;
                activeAnchor = preset.FixedAnchor;
            }
            SelectionError = null;
            bool grid = Grid20;
            waitingForSecondPoint = false;
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
            gesture.FixedPosition = bounds;
            if (!restorePositionOnly)
                OutputSettings = OutputSettings with { ScreenshotPaddingPixels = EffectivePadding(selectedOutput, preset) };
            SelectionMenu.SetPadding(OutputSettings.ScreenshotPaddingPixels);
            selectedInput = preset; selectedInputBounds = bounds; AspectRatio = CaptureAspectRatio.Free; gesture.AspectRatio = AspectRatio;
            lastPointerPosition = pointer;
            lastSnap = lastHeightLock = lastWidthLock = false;
            lastRectangleAction = CaptureKeyAction.None;
            if (!starting)
            {
                ShowSelectionOverlays();
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

    /// <summary>入力プリセットの設定を適用する。画面固定・カーソルが変わる場合は画面を取り直し、失敗・終了したらfalse。</summary>
    private async Task<bool> ApplyInputPresetSettingsAsync(Services.SavedRegion preset)
    {
        var next = preset.Apply(OutputSettings) with { ScreenshotPaddingPixels = EffectivePadding(selectedOutput, preset) };
        if (next.FreezeFrame == OutputSettings.FreezeFrame && next.IncludeCursor == OutputSettings.IncludeCursor)
        {
            OutputSettings = next;
            return true;
        }
        await RefreshSnapshotAsync(next);
        return SelectionError is null && !completed;
    }

    internal void ChangeAspectRatio(CaptureAspectRatio ratio)
    {
        if (!Enum.IsDefined(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio));
        AspectRatio = ratio; gesture.AspectRatio = ratio;
        SelectionMenu.SetRatio(ratio);
        UpdateMenu();
        RefreshOverlays();
    }

    internal void ChangePadding(int pixels)
    {
        if (pixels is < 0 or > SSCapture.Services.ScreenshotPadding.MaximumPixels) throw new ArgumentOutOfRangeException(nameof(pixels));
        temporaryPadding = pixels;
        OutputSettings = OutputSettings with { ScreenshotPaddingPixels = pixels };
        SelectionMenu.SetPadding(pixels);
        UpdateMenu();
    }

    internal void ApplyPreset(SSCapture.Services.CapturePreset preset)
    {
        OutputSettings = preset.Apply(OutputSettings) with { ScreenshotPaddingPixels = EffectivePadding(preset, selectedInput) };
        selectedOutput = preset;
        Destination = AllowedDestination(OutputSettings.DefaultDestination);
        SelectionMenu.SetDestination(Destination);
        SelectionMenu.SetPadding(OutputSettings.ScreenshotPaddingPixels);
        SelectionMenu.SetApplication(SelectedEditorId);
        UpdateMenu();
    }

    internal void ChangeApplication(string id)
    {
        SSCapture.Services.ExternalApplication.ValidateReference(EditorApplications, id);
        OutputSettings = IsScreenshot ? OutputSettings with { SelectedImageEditorId = id } : OutputSettings with { SelectedVideoEditorId = id };
        SelectionMenu.SetApplication(id);
        UpdateMenu();
    }

    internal void ChangeGrid20(bool enabled)
    {
        try
        {
            gridChanged?.Invoke(enabled);
            gesture.Grid20 = enabled;
            SelectionMenu.SetGrid(enabled);
            if (lastPointerPosition is Point point) gesture.Move(point, lastSnap, lastHeightLock, lastWidthLock, lastRectangleAction);
            RefreshOverlays();
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void ChangeMode(CaptureMode mode) => ChangeMode(mode, false);

    private void ChangeMode(CaptureMode mode, bool preferWgc)
    {
        bool isolated = mode == CaptureMode.Window && preferWgc && IsScreenshot && findWindow is not null;
        if (completed || mode == CaptureMode.Scroll && !options.AllowScroll || mode == Mode && isolated == exclusiveWindow) return;
        try
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            bool grid20 = Grid20;
            Point pointer = menuOwner is not null ? menuPointer : gesture.RawCurrent;
            waitingForSecondPoint = false;
            gesture.Reset();
            ReleasePointer();
            Mode = mode;
            exclusiveWindow = isolated;
            gesture = CreateGesture(mode, grid20);
            if (mode == CaptureMode.FixedRegion && selectedInputBounds is Rectangle fixedBounds && fixedBounds.Size == activeFixedSize)
                gesture.FixedPosition = fixedBounds;
            SelectionMenu.SetMode(mode);
            SelectionMenu.SetRatio(AspectRatio);
            UpdateMenu();
            SelectionMenu.CloseForCapture();
            if (mode == CaptureMode.FullScreen)
            {
                CompleteSelection(new CaptureSelection(selectionBounds ?? Snapshot.Bounds, null, Destination));
                return;
            }
            lastPointerPosition = null;
            gesture.Move(pointer);
            ReplaceGuide();
            RefreshOverlays();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void ToggleWindowCaptureMode(bool preferWgc)
    {
        if (gesture.IsPressed) return;
        if (Mode == CaptureMode.Window)
        {
            ChangeMode(modeBeforeWindowShortcut ?? CaptureMode.Rectangle);
            modeBeforeWindowShortcut = null;
        }
        else
        {
            modeBeforeWindowShortcut = Mode;
            ChangeMode(CaptureMode.Window, preferWgc);
        }
    }

    private void ReplaceGuide(CaptureOverlay? preferredOwner = null)
    {
        guide?.Dispose();
        guide = null;
        if (overlays.Count == 0 || !ShowSelectionGuide) return;
        CaptureOverlay owner = preferredOwner ?? menuOwner ?? overlays.FirstOrDefault(o => o.MonitorBounds.Contains(gesture.RawCurrent)) ?? overlays[0];
        Rectangle display = ResolveGuideMonitor(OutputSettings.SelectionGuideDisplay, Screen.PrimaryScreen?.Bounds, CaptureMonitorForGuide);
        guide = new UI.SelectionGuideForm(display, options.Intent, () => { if (!refreshing) ApplicationDeactivated(); },
            Mode == CaptureMode.FixedRegion ? activeFixedSize : null, HandleCommandKey, activeAnchor, AltKeyChanged, Mode,
            dismiss: () => ChangeGuideVisibility(false), spaceEnabled: OutputSettings.CaptureKeys.SpaceEnabled,
            workingArea: Screen.GetWorkingArea(display), beforeKeys: OutputSettings.CaptureBeforeKeys,
            dragKeys: OutputSettings.CaptureKeys, mouse: OutputSettings.CaptureMouse, canCrossMonitors: CanGuideCrossMonitors,
            selectionMethod: OutputSettings.SelectionMethod,
            controlActivation: OutputSettings.ControlActivation, windowActivation: OutputSettings.WindowActivation,
            registeredFrame: gesture.FixedPosition is not null, fixedWheelEnabled: FixedWheelEnabled);
        guide.SetShared(OutputSettings.ShareCapturePresentation);
        guide.Show(owner);
        WatchGuideBounds(guide);
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

    private bool CanGuideCrossMonitors => OutputSettings.LimitToStartingMonitor && Snapshot.Monitors.Count > 1;

    private void UpdateGuidePointer()
    {
        if (!ShowSelectionGuide || guide is null) return;
        guide.UpdateOperations(Dragging, CanGuideCrossMonitors);
        guide.UpdatePointer(gesture.RawCurrent, gesture.IsPressed && gesture.HasDragged
            || HasControlDrag || FreeDesktopDrag && targetDrag?.Path.Count > 1);
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
            ignoredModifiers = CaptureNative.Modifiers();
            focusBeforeSelection = CaptureNative.GetForegroundWindow();
            if (Mode == CaptureMode.FullScreen)
            {
                CompleteSelection(new CaptureSelection(Snapshot.Bounds, null, Destination));
                return;
            }
            foreach (Rectangle bounds in SelectionMonitors) overlays.Add(new CaptureOverlay(this, bounds));
            foreach (CaptureOverlay overlay in overlays) overlay.Show();
            MoveTo(initialPointer);
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
                if (captureWindowsKey)
                {
                    captureStartForeground = CaptureNative.GetForegroundWindow();
                    windowsKey = new CaptureWindowsKey(
                        () => HasPointerPress || menuOpen || menuTransition || refreshing || SelectingControl,
                        OwnsSelectionInputWithoutFocus);
                }
                // Key messages can be routed to the movable guide or a captured
                // overlay. Only while selecting, poll Alt to cover either route.
                snapTimer = new System.Windows.Forms.Timer { Interval = 16 };
                snapTimer.Tick += (_, _) =>
                {
                    if (windowsKey?.TakeEscape() == true) HandleCommandKey(Keys.Escape);
                    nint foreground = SelectingControl ? CaptureNative.GetForegroundWindow() : 0;
                    if (foreground != 0 && foreground != focusBeforeSelection && !CaptureNative.OwnsForegroundWindow())
                    {
                        Cancel();
                        return;
                    }
                    ModifiersChanged();
                    RefreshLiveWindowMargin();
                    if (overlayUpdatePending) UpdateOverlays();
                };
                snapTimer.Start();
            }
            Move();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private bool OwnsSelectionInputWithoutFocus()
    {
        nint foreground = CaptureNative.GetForegroundWindow();
        if (SelectingControl && foreground == focusBeforeSelection) return true;
        return CaptureNative.TryCursorPosition(out Point pointer)
            && OwnsCapturePointer(foreground, CaptureNative.WindowFromPoint(pointer));
    }

    internal bool OwnsCapturePointer(nint foreground, nint windowAtPointer)
    {
        if (starting || refreshing || completed || menuOpen || menuTransition || windowAtPointer == 0
            || foreground != 0 && foreground != captureStartForeground) return false;
        foreach (CaptureOverlay overlay in overlays)
            if (overlay.Visible && overlay.IsHandleCreated && overlay.Handle == windowAtPointer) return true;
        return false;
    }

    internal void Move(bool queuedKeyState = false, Point? eventPosition = null, Keys? mouseModifiers = null)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
        try
        {
            Point position = eventPosition ?? CaptureNative.CursorPosition();
            // キュー内のDown/Upを現在位置で読み替えると、高速ドラッグがクリックに化ける。
            ApplyPointerInput(position, ReadModifiers(queuedKeyState && !SelectingControl, mouseModifiers),
                windowsKey?.TakeTap() == true, forceMove: true);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void MoveTo(Point physicalPosition, bool snap = false, bool lockHeight = false, bool lockWidth = false,
        CaptureKeyAction rectangleAction = CaptureKeyAction.None)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
        if (!FreeDesktopDrag && selectionBounds is Rectangle bounds)
            physicalPosition = new Point(Math.Clamp(physicalPosition.X, bounds.Left, bounds.Right - 1),
                Math.Clamp(physicalPosition.Y, bounds.Top, bounds.Bottom - 1));
        // Keyboard nudges must survive synthetic mouse messages at the unchanged cursor
        // location, including the click used to confirm an adjusted fixed rectangle.
        if (Mode == CaptureMode.FixedRegion && lastPointerPosition == physicalPosition && lastSnap == snap) return;
        lastPointerPosition = physicalPosition;
        lastSnap = snap;
        lastHeightLock = lockHeight;
        lastWidthLock = lockWidth;
        lastRectangleAction = rectangleAction;
        if (precisePointer != physicalPosition) precisePointer = null;
        bool precise = precisePointer.HasValue;
        gesture.Move(physicalPosition, snap && !precise, lockHeight, lockWidth, rectangleAction, precise);
        MoveControlDrag(gesture.RawCurrent);
        targetDrag?.Move(physicalPosition, Snapshot.Monitors, FreeDesktopDrag ? null : findWindow);
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
                    foreach (Rectangle monitor in SelectionMonitors) ShowMonitorOverlay(monitor);
            }
        }
        else if (!starting && cursorConfinement is null) cursorConfinement = confineCursor(bounds);
    }

    /// <summary>選択対象のモニターのオーバーレイだけを表示する。足りないものは追加し、対象外は隠す。</summary>
    private void ShowSelectionOverlays()
    {
        foreach (var overlay in overlays) { if (SelectionMonitors.Contains(overlay.MonitorBounds)) overlay.Show(); else overlay.Hide(); }
        foreach (Rectangle monitor in SelectionMonitors.Where(m => overlays.All(o => o.MonitorBounds != m)))
        {
            var overlay = new CaptureOverlay(this, monitor);
            overlays.Add(overlay);
            overlay.Show();
        }
    }

    private CaptureOverlay ShowMonitorOverlay(Rectangle monitor)
    {
        var overlay = overlays.FirstOrDefault(candidate => candidate.MonitorBounds == monitor);
        if (overlay is null)
        {
            overlay = new CaptureOverlay(this, monitor);
            overlays.Add(overlay);
        }
        if (!overlay.Visible) overlay.Show();
        return overlay;
    }

    private void FollowIdleMonitorPointer(Point pointer)
    {
        var monitor = Snapshot.Monitors.FirstOrDefault(candidate => candidate.Contains(pointer));
        if (monitor.IsEmpty || selectionBounds == monitor) return;
        selectionBounds = monitor;
        SelectionMonitors = [monitor];
        gesture.UpdateTargets(SelectionMonitors, windows, monitor);
        lastPointerPosition = null;
        if (starting) return;

        menuOwner = null;
        var owner = ShowMonitorOverlay(monitor);
        owner.Activate();
        owner.Focus();
        foreach (var overlay in overlays)
            if (overlay != owner && overlay.Visible) overlay.Hide();
        TrackGuideMonitor(pointer, reposition: false);
        ReplaceGuide(owner);
    }

    internal void ModifiersChanged() => UpdateModifiers(false);
    internal void AltKeyChanged() => UpdateModifiers(true);

    internal static CaptureKeyAction ScrollSelectionAction(CaptureKeyAction action) => action switch
    {
        CaptureKeyAction.SelectDesktop => CaptureKeyAction.CrossMonitor,
        _ when CaptureKeyMap.IsControlSelection(action) || CaptureKeyMap.IsWindowSelection(action) => CaptureKeyAction.None,
        _ => action
    };

    private void UpdateModifiers(bool queuedKeyState)
    {
        if (completed || refreshing || menuOpen || menuTransition) return;
        try
        {
            if (Control.MouseButtons == MouseButtons.None) suppressedButtons = MouseButtons.None;
            ApplyPointerInput(CaptureNative.CursorPosition(), ReadModifiers(queuedKeyState), windowsKey?.TakeTap() == true);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal void ApplyPointerInput(Point pointer, CaptureModifiers modifiers, bool windowsTap = false, bool forceMove = false)
    {
        if (confirmingControl) return;
        windowsKey?.ObservePointer(pointer);
        bool dragging = Dragging;
        var bindings = dragging ? OutputSettings.CaptureKeys : OutputSettings.CaptureBeforeKeys;
        CaptureKeyAction action = ResolveCaptureAction(bindings, modifiers, dragging);
        CaptureKeyAction beforeAction = OutputSettings.CaptureBeforeKeys.Resolve(modifiers);
        if (Mode == CaptureMode.Scroll)
        {
            beforeAction = ScrollSelectionAction(beforeAction);
            action = ScrollSelectionAction(action);
        }
        CaptureKeyAction modeTap = Mode == CaptureMode.Scroll ? CaptureKeyAction.None : TakeBeforeModeTap(pointer, modifiers, beforeAction, windowsTap);
        if (modeTap != CaptureKeyAction.None) hasSelectionInput = true;
        if (modeTap == CaptureKeyAction.SelectControlToggle) controlToggled = !controlToggled;
        if (Mode != CaptureMode.Scroll) UpdateBeforeWindowMode(modifiers, beforeAction, modeTap == CaptureKeyAction.HighlightWindowToggle);
        UpdateAltSnapTap(pointer, modifiers);
        bool wasDesktopHeld = desktopHeld;
        desktopHeld = !dragging && beforeAction == CaptureKeyAction.SelectDesktop;
        bool idleMonitorCommand = action is CaptureKeyAction.SelectDesktop or CaptureKeyAction.CrossMonitor;
        bool movingWhileIdle = !HasPointerPress && !dragging && selectionBounds is not null
            && Snapshot.Monitors.Count > 1 && idleMonitorCommand;
        bool changed = modifiers != activeModifiers || dragging != activeDrag || idleMonitorMove != movingWhileIdle || wasDesktopHeld != desktopHeld;
        string? hint = altSnapEnabled ? "ウィンドウスナップ ON" : null;
        if (SelectionGuideText.Applies(action, Mode, dragging, CanGuideCrossMonitors))
            hint = $"{CaptureKeyMap.Label(modifiers)} : {CaptureKeyMap.Describe(action)}";
        if (!HasPointerPress && modifiers == CaptureModifiers.Win && action == CaptureKeyAction.HighlightWindowToggle
            && (windowsKey?.CanTap ?? true))
            hint += "\n" + CaptureKeyMap.Label(CaptureModifiers.Win) + (HighlightWindow
                ? "を離すと元の撮影モードに戻る" : "を離すとウィンドウ撮影モードに変更");
        CaptureKeyAction pressedAction = windowsTap && !HasPointerPress && !dragging ? bindings.Win
            : (modifiers & ~activeModifiers) != 0 && modifiers != CaptureModifiers.Win ? action : CaptureKeyAction.None;
        if (changed)
        {
            activeModifiers = modifiers;
            activeDrag = dragging;
            if (modifiers != CaptureModifiers.None) hasSelectionInput = true;
        }
        if (!dragging && !SelectingDesktop && (controlToggled || beforeAction == CaptureKeyAction.SelectControlPush))
            hint = "コントロールを選択";
        else if (!dragging && windowPushReturn is not null)
            hint = "ウィンドウ撮影中\nキーを離すと元の撮影モードに戻る";
        bool hintChanged = ModifierHint != hint;
        ModifierHint = hint;
        bool selectControl = dragging ? action == CaptureKeyAction.SelectControl
            : controlToggled || beforeAction == CaptureKeyAction.SelectControlPush;
        UpdateControlSelection(Mode != CaptureMode.Scroll && (selectControl || HasControlDrag) && IsScreenshot
            && !SelectingDesktop && !HasWindowDrag && suppressedButtons == MouseButtons.None, pointer, preview: !dragging);
        bool snap = altSnapEnabled || action == CaptureKeyAction.Snap;
        bool height = action == CaptureKeyAction.LockHeight;
        bool width = action == CaptureKeyAction.LockWidth;
        if ((idleMonitorMove || movingWhileIdle) && !HasPointerPress && !dragging && selectionBounds is not null)
            FollowIdleMonitorPointer(pointer);
        else if (!HasPointerPress && !dragging && selectionBounds is null && idleMonitorCommand)
            TrackGuideMonitor(pointer, reposition: true);
        idleMonitorMove = movingWhileIdle;
        if (movingWhileIdle)
        {
            cursorConfinement?.Dispose();
            cursorConfinement = null;
        }
        else if (!FreeDesktopDrag) UpdateMonitorRestriction(pointer, action == CaptureKeyAction.CrossMonitor);
        CaptureKeyAction rectangleAction = dragging && action is CaptureKeyAction.LockAspectRatio
            or CaptureKeyAction.FromCenter or CaptureKeyAction.FromCenterWithAspectRatio or CaptureKeyAction.Grid ? action : CaptureKeyAction.None;
        if (forceMove || pointer != lastPointerPosition || snap != lastSnap || height != lastHeightLock || width != lastWidthLock
            || rectangleAction != lastRectangleAction)
            MoveTo(pointer, snap, height, width, rectangleAction);
        if (pressedAction == CaptureKeyAction.SwitchAnchor) SwitchSelectionAnchor();
        if (changed || hintChanged || modeTap != CaptureKeyAction.None) RefreshOverlays();
    }

    private void UpdateAltSnapTap(Point pointer, CaptureModifiers modifiers)
    {
        if (HasPointerPress || OutputSettings.CaptureBeforeKeys.Alt != CaptureKeyAction.Snap)
        { altSnapPress = null; return; }

        if (modifiers == CaptureModifiers.None && activeModifiers == CaptureModifiers.Alt && altSnapPress is { } press
            && CaptureWindowsKey.IsStationaryTap(press.Pointer, pointer, Environment.TickCount64 - press.Time, false))
            altSnapEnabled = !altSnapEnabled;

        if (modifiers == CaptureModifiers.Alt && activeModifiers == CaptureModifiers.None)
            altSnapPress = (pointer, Environment.TickCount64);
        else if (modifiers != CaptureModifiers.Alt || altSnapPress?.Pointer != pointer)
            altSnapPress = null;
    }

    private static CaptureKeyAction ResolveCaptureAction(CaptureKeyMap bindings, CaptureModifiers modifiers, bool dragging)
    {
        CaptureKeyAction action = bindings.Resolve(modifiers);
        return dragging && action == CaptureKeyAction.HighlightWindow
            ? CaptureKeyAction.None : action;
    }

    internal bool HandleCommandKey(Keys keyData) => HandleCommandKey(keyData, false);

    internal bool HandleCommandKey(Keys keyData, bool repeated)
    {
        if (refreshing) return true;
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            if (repeated || suppressedButtons != MouseButtons.None) return true;
            if (HasPointerPress || gesture.IsPressed || SelectingControl) AbortSelection();
            else FinishCommand(false, escapedBeforeSelectionInput: !hasSelectionInput && !menuOpen && !menuTransition);
            return true;
        }
        if (completed || menuOpen || menuTransition) return false;
        if (confirmingControl) return true;
        if ((keyData & Keys.KeyCode) == Keys.Space && OutputSettings.CaptureKeys.SpaceEnabled)
            return repeated || SwitchSelectionAnchor();
        if (Mode == CaptureMode.FixedRegion && keyData == Keys.Enter)
        {
            hasSelectionInput = true;
            CaptureSelection? selection = gesture.FixedSelection(Destination);
            if (selection is not null) CompleteSelection(selection);
            return true;
        }
        return NudgeCursor(keyData);
    }

    private bool SwitchSelectionAnchor()
    {
        if (SelectingControl || SelectingDesktop || !gesture.Space(captureWindowsKey ? MovePointerToCorner : null,
            OutputSettings.CaptureKeys.SpaceCycle)) return false;
        hasSelectionInput = true;
        lastPointerPosition = gesture.RawCurrent;
        activeAnchor = gesture.FixedAnchor;
        guide?.UpdateAnchor(activeAnchor);
        UpdateMenu();
        RefreshOverlays();
        return true;
    }

    private Point? MovePointerToCorner(Point corner)
    {
        // A rectangle's right/bottom are exclusive. At a desktop edge use the nearest on-screen pixel.
        Point target = SelectionMonitors.Select(monitor => new Point(
                Math.Clamp(corner.X, monitor.Left, monitor.Right - 1),
                Math.Clamp(corner.Y, monitor.Top, monitor.Bottom - 1)))
            .MinBy(point => Math.Pow((double)point.X - corner.X, 2) + Math.Pow((double)point.Y - corner.Y, 2));
        return CaptureNative.SetPhysicalCursorPos(target.X, target.Y) ? CaptureNative.CursorPosition() : null;
    }

    private void RefreshOverlays()
    {
        UpdateWindowPreview();
        if (UsesClientWindowArea && !SelectingControl) ResetTargetMargin();
        else UpdateTargetMargin();
        UpdateGuidePointer();
        overlayUpdatePending = true;
        if (snapTimer?.Enabled != true) UpdateOverlays();
    }

    private void UpdateOverlays()
    {
        overlayUpdatePending = false;
        if (!starting && !refreshing && (windowPreview is not null || FreeDesktopDrag || auxiliaryOverlays.Count != 0))
        {
            Rectangle previewBounds = PreviewFrame?.Bounds ?? Rectangle.Empty;
            bool NeedsAuxiliary(Rectangle monitor) => FreeDesktopDrag ? targetDrag?.Monitors.Contains(monitor) == true
                : !previewBounds.IsEmpty && previewBounds.IntersectsWith(monitor);
            foreach (CaptureOverlay overlay in auxiliaryOverlays.ToArray())
            {
                if (SelectionMonitors.Contains(overlay.MonitorBounds)) auxiliaryOverlays.Remove(overlay);
                else if (!NeedsAuxiliary(overlay.MonitorBounds))
                { overlay.Hide(); auxiliaryOverlays.Remove(overlay); }
            }
            foreach (Rectangle monitor in Snapshot.Monitors)
                if (NeedsAuxiliary(monitor) && !SelectionMonitors.Contains(monitor))
                    auxiliaryOverlays.Add(ShowMonitorOverlay(monitor));
        }
        var dirty = overlays.Where(overlay => overlay.Visible && !overlay.IsDisposed && overlay.InvalidateChanges()).ToArray();
        // WM_PAINTをウィンドウごとの待ち行列へ任せると、後から追加した画面に偏る。
        foreach (CaptureOverlay overlay in dirty)
            if (!overlay.IsDisposed) overlay.Update();
    }

    private async void UpdateWindowPreview()
    {
        if (completed || refreshing) return;
        bool control = SelectingControl;
        bool clientOnly = !control && UsesClientWindowArea;
        CaptureWindow? target = control ? (UsesControlPreview || UsesWindowSource ? controlCandidate?.Window : null)
            : UsesWindowSource && !SelectingDesktop && !HasWindowDrag
                ? findWindow?.Invoke(targetDrag is { Path.Count: > 0 } ? targetDrag.Path[0]
                    : clientOnly ? gesture.WindowTargetPoint : Pointer) : null;
        if (target == windowPreview?.Target && (windowPreview is null ||
            windowPreview.IncludeCursor == OutputSettings.IncludeCursor && windowPreviewIsControl == control
            && windowPreview.ClientAreaOnly == clientOnly)) return;
        ClearWindowPreview();
        if (target is null) return;

        windowPreviewIsControl = control;
        var pending = windowPreview = new WindowCapturePreview(target, OutputSettings.IncludeCursor,
            control || clientOnly ? [target] : findWindowGroup(target), clientOnly);
        try { await pending.Frame; }
        catch (Exception) when (pending.Frame.IsCanceled || pending.Frame.IsFaulted) { }
        if (ReferenceEquals(windowPreview, pending)) RefreshOverlays();
    }

    private Rectangle VisibleClientBounds()
    {
        if (findWindow?.Invoke(gesture.WindowTargetPoint) is not { } window) return Rectangle.Empty;
        if (OutputSettings.FreezeFrame) return window.ClientBoundsAtSelection ?? Rectangle.Empty;
        try { return CaptureNative.RequireClientBounds(window); }
        catch (InvalidOperationException) { return Rectangle.Empty; }
    }

    private void ClearWindowPreview()
    {
        WindowCapturePreview? old = windowPreview;
        windowPreview = null;
        windowPreviewIsControl = false;
        old?.Dispose();
    }

    internal void MouseDown(CaptureOverlay overlay, MouseButtons button, Point? eventPosition = null, Keys? mouseModifiers = null)
    {
        if (menuTransition && !menuOpen)
        {
            if (menuClosedByCommand && menuOwner is { } owner)
                RestoreAfterMenu(owner, menuGeneration, fromOverlayPress: true);
            else
            {
                // WinForms can pass the outside press to the overlay after closing the menu.
                if (overlay != dragOwner && !overlay.IsDisposed) overlay.Capture = false;
                if (menuOwner is { } outsideOwner)
                    RestoreAfterMenu(outsideOwner, menuGeneration, fromOverlayPress: true);
                return;
            }
        }
        if (completed || refreshing || menuOpen || menuTransition) return;
        if (suppressedButtons != MouseButtons.None) return;
        if (confirmingControl) return;
        bool choosingEnd = waitingForSecondPoint && !HasPointerPress;
        var beforeClick = OutputSettings.CaptureMouse.Before(button);
        bool selectEnd = choosingEnd && beforeClick == CaptureClickAction.Select;
        var click = HasPointerPress || choosingEnd && !selectEnd ? OutputSettings.CaptureMouse.During(button) : beforeClick;
        if (button != pressedButton && click == CaptureClickAction.ToggleMagnifier)
        {
            hasSelectionInput = true;
            ChangeMagnifierVisibility(!OutputSettings.ShowMagnifier);
            if (overlay != dragOwner) overlay.Capture = false;
            return;
        }
        if (HasPointerPress || choosingEnd && !selectEnd)
        {
            if (button != pressedButton && click == CaptureClickAction.Cancel)
            {
                AbortSelection();
                suppressedButtons |= button;
            }
            if (overlay != dragOwner) overlay.Capture = false;
            return;
        }

        if (click == CaptureClickAction.Menu)
        {
            hasSelectionInput = true;
            ShowMenu(overlay, eventPosition);
            return;
        }
        if (click == CaptureClickAction.None || Mode == CaptureMode.Scroll && click == CaptureClickAction.Desktop)
        {
            overlay.Capture = false;
            return;
        }

        hasSelectionInput = true;
        controlConfirmationError = null;
        Move(true, eventPosition, mouseModifiers);
        if (completed) return;
        desktopPress = desktopHeld || click == CaptureClickAction.Desktop;
        if (desktopPress)
        {
            waitingForSecondPoint = false;
            gesture.Reset();
            ExitControlSelection();
        }
        dragOwner = overlay;
        pressedButton = button;
        altSnapPress = null;
        if (!desktopPress && SelectingControl)
        {
            BeginControlPress(overlay);
            return;
        }
        if (FreeDesktopDrag || !desktopPress && IsScreenshot && HighlightWindow && !UsesClientWindowArea && findWindow is not null)
        {
            targetDrag = new CaptureTargetDrag(findWindowGroup);
            targetDrag.Move(gesture.RawCurrent, Snapshot.Monitors, FreeDesktopDrag ? null : findWindow);
        }
        if (FreeDesktopDrag)
        {
            cursorConfinement?.Dispose();
            cursorConfinement = null;
        }
        if (!desktopPress && !TwoPointSelection)
        {
            CaptureModifiers modifiers = ReadModifiers(true, mouseModifiers);
            CaptureKeyAction action = ResolveCaptureAction(OutputSettings.CaptureKeys, modifiers, dragging: true);
            bool snapStart = altSnapEnabled || action == CaptureKeyAction.Snap || OutputSettings.CaptureBeforeKeys.Resolve(modifiers) == CaptureKeyAction.Snap;
            gesture.Begin(gesture.RawCurrent, CaptureNative.DragThreshold(overlay.DeviceDpi),
                snapStart, action == CaptureKeyAction.LockHeight, action == CaptureKeyAction.LockWidth, action);
        }
        overlay.Capture = true;
        if (!overlay.Capture) Cancel();
        else RefreshOverlays();
    }

    internal void MouseUp(MouseButtons button, Point? eventPosition = null, Keys? mouseModifiers = null)
    {
        if ((suppressedButtons & button) != 0) { suppressedButtons &= ~button; return; }
        if (completed || refreshing || menuOpen || menuTransition || button != pressedButton || pressedButton == MouseButtons.None) return;
        Move(true, eventPosition, mouseModifiers);
        // A modifier release can cancel the gesture while processing this button-up message.
        suppressedButtons &= ~button;
        if (completed || pressedButton != button) return;
        if (!desktopPress && SelectingControl)
        {
            if (controlDrag is { } drag) ConfirmControlDrag(drag);
            else ConfirmControl(Pointer);
            return;
        }
        if (!desktopPress && TwoPointSelection)
        {
            OnTwoPointClick();
            return;
        }
        CaptureSelection? selection = desktopPress ? SelectDesktopMonitors()
            : HasWindowDrag && targetDrag!.Windows.Count != 0 ? SelectDraggedWindows()
            : Mode == CaptureMode.Scroll && !gesture.HasDragged ? null
            : gesture.End(gesture.RawCurrent, Destination, lastSnap && precisePointer is null,
                lastHeightLock, lastWidthLock, lastRectangleAction, precisePointer.HasValue);
        if (selection is not null && !UsesClientWindowArea) selection = ApplyWindowMargin(selection);
        bool restoreConfinement = FreeDesktopDrag;
        bool entirelyDesktop = desktopPress;
        ReleasePointer();
        if (selection is null)
        {
            if (restoreConfinement && !desktopHeld) UpdateMonitorRestriction(Pointer, false);
            gesture.Reset(); RefreshOverlays();
        }
        else CompleteSelection(selection, entireMonitor: entirelyDesktop);
    }

    private CaptureSelection? SelectDesktopMonitors()
    {
        if (!FreeDesktopDrag || targetDrag is not { Monitors.Count: > 0 }) return gesture.MonitorSelection(Pointer, Destination);
        Rectangle[] regions = targetDrag.Monitors.ToArray();
        return new(regions.Aggregate(Rectangle.Union), null, Destination, Composite: regions.Length > 1 ? new(Regions: regions) : null);
    }

    private CaptureSelection SelectDraggedWindows()
    {
        CaptureWindow[] roots = targetDrag!.Windows.ToArray();
        return new(roots.Select(root => root.Bounds).Aggregate(Rectangle.Union), null, Destination,
            Composite: new(WindowRoots: roots));
    }

    private CaptureModifiers ReadModifiers(bool queuedKeyState, Keys? mouseModifiers = null)
    {
        Keys modifiers = CaptureNative.Modifiers(queuedKeyState);
        if (mouseModifiers is Keys messageState) modifiers = (modifiers & Keys.Alt) | messageState;
        ignoredModifiers &= modifiers;
        return CaptureKeyMap.FromKeys(modifiers & ~ignoredModifiers) |
            (windowsKey?.IsHeld == true ? CaptureModifiers.Win : CaptureModifiers.None);
    }

    internal void AbortSelection()
    {
        hasSelectionInput = true;
        suppressedButtons |= pressedButton;
        if (SelectingControl) controlHoldCancelled = true;
        ResetTargetMargin();
        controlConfirmationError = null;
        controlToggled = false;
        pendingModeTap = null;
        ExitControlSelection();
        waitingForSecondPoint = false;
        gesture.Reset();
        bool restoreConfinement = FreeDesktopDrag;
        ReleasePointer();
        if (restoreConfinement && !desktopHeld) UpdateMonitorRestriction(Pointer, false);
        RefreshOverlays();
    }

    private void ShowMenu(CaptureOverlay overlay, Point? eventPosition)
    {
        try
        {
            Move(true, eventPosition);
            if (completed) return;
            menuPointer = gesture.RawCurrent;
            menuTransition = true;
            controlToggled = false;
            pendingModeTap = null;
            RestoreWindowPush();
            ExitControlSelection();
            gesture.Reset(keepFixedWheel: true);
            ReleasePointer();
            // WinForms may have implicitly captured the right button before OnMouseDown.
            overlay.Capture = false;
            RefreshOverlays();
            menuOwner = overlay;
            menuGeneration++;
            menuOpen = true;
            UpdateMenu();
            SelectionMenu.Show(overlay, overlay.PointToClient(menuPointer));
            if (menuOpen)
            {
                // A canceled opening does not raise Closed.
                menuOpen = SelectionMenu.Visible;
                menuTransition = false;
                if (!menuOpen) { menuOwner = null; RefreshOverlays(); }
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void MenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        if (!menuOpen) return;
        menuOpen = false;
        if (completed || refreshing || starting) return;
        menuTransition = true;
        menuClosedByCommand = e.CloseReason == ToolStripDropDownCloseReason.ItemClicked;
        if (menuOwner is not { IsDisposed: false } owner) { Cancel(); return; }

        int generation = menuGeneration;
        // The mouse message that closed the menu can still reach the overlay.
        owner.BeginInvoke((Action)(() => RestoreAfterMenu(owner, generation)));
    }

    private void RestoreAfterMenu(CaptureOverlay owner, int generation, bool fromOverlayPress = false)
    {
        if (completed || refreshing || menuOpen || !menuTransition
            || generation != menuGeneration || owner != menuOwner || owner.IsDisposed) return;
        if (!fromOverlayPress && !CaptureNative.OwnsForegroundWindow()
            && !(menuClosedByCommand && CaptureNative.ForegroundTransition))
        {
            Cancel();
            return;
        }
        menuTransition = false;
        menuOwner = null;
        var target = owner.Visible ? owner : overlays.FirstOrDefault(overlay => overlay.Visible && !overlay.IsDisposed);
        target?.Activate();
        target?.Focus();
        ResumePointerAfterMenu();
    }

    internal void CaptureLost(CaptureOverlay overlay)
    {
        if (completed || menuOpen || menuTransition || pressedButton == MouseButtons.None || overlay != dragOwner || overlay.IsDisposed) return;
        try
        {
            overlay.BeginInvoke((Action)(() =>
            {
                if (completed || menuOpen || menuTransition || !HasPointerPress || dragOwner != overlay || overlay.IsDisposed || overlay.Capture)
                    return;
                if (CaptureNative.OwnsForegroundWindow() || SelectingControl) AbortSelection();
                else Cancel();
            }));
        }
        catch (InvalidOperationException) { Cancel(); }
    }

    internal void ApplicationDeactivated()
    {
        if (starting || refreshing || completed || SelectingControl) return;
        if (!menuOpen && !menuTransition) { Cancel(); return; }
        if (menuOwner is not { IsDisposed: false } owner) return;

        int generation = menuGeneration;
        owner.BeginInvoke((Action)(() =>
        {
            if (completed || refreshing || generation != menuGeneration || CaptureNative.OwnsForegroundWindow()) return;
            Cancel();
        }));
    }

    private void ResumePointerAfterMenu()
    {
        if (Mode == CaptureMode.FixedRegion)
        {
            gesture.ResumeFixedPointer();
            lastPointerPosition = null;
        }
        Move();
    }

    internal void Cancel() => FinishCommand(false);

    private void ReleasePointer()
    {
        pressedButton = MouseButtons.None;
        desktopPress = false;
        targetDrag = null;
        CaptureOverlay? owner = dragOwner;
        dragOwner = null;
        if (owner is not null && !owner.IsDisposed) owner.Capture = false;
    }

    private void CloseOverlays()
    {
        try
        {
            ExitControlSelection(restoreOverlayFocus: false);
            ClearWindowPreview();
            snapTimer?.Stop();
            snapTimer?.Dispose();
            snapTimer = null;
            windowsKey?.Dispose();
            windowsKey = null;
            ReleasePointer();
            gesture.Reset();
            SelectionMenu.CloseForCapture();
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
        CaptureReplaySource.Control? control = selection.ControlSource;
        var controlGroup = selection.ReplaySource as CaptureReplaySource.ControlGroup;
        IReadOnlyList<CaptureWindow>? roots = selection.Composite?.WindowRoots;
        CaptureWindow? target = control?.Window ?? roots?.FirstOrDefault() ?? (selection.WindowHitPoint is Point hit ? findWindow?.Invoke(hit) : null);
        if (selection.WindowHitPoint is Point hitPoint) selection = selection with { WindowTitle = target?.Title ?? readTitle?.Invoke(hitPoint) };
        bool useWindowSource = (controlGroup?.Isolated ?? control?.Isolated ?? UsesWindowSource) && !entireMonitor;
        bool clientOnly = UsesClientWindowArea && control is null && controlGroup is null && !entireMonitor && selection.WindowHitPoint is not null;
        if (clientOnly) selection = selection with { TargetMargin = null };
        bool singleWindow = roots is null && selection.WindowHitPoint is not null && (Mode == CaptureMode.Rectangle || HighlightWindow);
        bool groupWindows = !clientOnly && control is null && controlGroup is null && selection.TargetMargin is null && target is not null && HighlightWindow && !entireMonitor && IsScreenshot
            && (useWindowSource || !singleWindow);
        CaptureWindow[] group = groupWindows ? [.. (roots ?? [target!]).SelectMany(findWindowGroup)
            .DistinctBy(window => window.Handle).OrderBy(window => window.ZOrder)] : [];
        if (roots is not null)
            selection = selection with { WindowTitle = roots.Count == 1 ? target?.Title : $"ウィンドウ {roots.Count} 件" };
        if (!useWindowSource && (group.Length > 1 || roots is not null))
        {
            Rectangle[] regions = [.. group.SelectMany(window => Snapshot.Monitors.Select(monitor => Rectangle.Intersect(monitor, window.Bounds)))
                .Where(CaptureGeometry.IsUsable).Distinct()];
            if (regions.Length == 0) return;
            selection = selection with { Bounds = regions.Aggregate(Rectangle.Union), Composite = new(Regions: regions) };
        }
        int? targetOffset = control?.TargetMargin ?? selection.TargetMargin;
        if (!clientOnly && !entireMonitor && !(useWindowSource && targetOffset.HasValue)
            && selection.Composite is null && selectionBounds is Rectangle bounds && (!bounds.Contains(selection.Bounds)
            || selection.Polygon?.Any(point => !bounds.Contains(point)) == true)) return;
        if (control is null && controlGroup is null && Mode == CaptureMode.FixedRegion && !entireMonitor
            && (selection.Polygon is not null || gesture.FixedSelection(Destination)?.Bounds != selection.Bounds)) return;
        if (IsScreenshot)
        {
            if (controlGroup is null && (entireMonitor || Mode == CaptureMode.FullScreen))
            {
                IReadOnlyList<Rectangle> monitors = selection.Composite?.Regions ??
                    Snapshot.Monitors.Where(selection.Bounds.Contains).ToArray();
                selection = selection with { ReplaySource = new CaptureReplaySource.Monitors(monitors) };
            }
            else if (control is not null)
                selection = selection with { ReplaySource = control };
            else if (target is not null)
                selection = selection with { ReplaySource = new CaptureReplaySource.Windows(roots ?? [target], useWindowSource, groupWindows, singleWindow)
                    { TargetMargin = selection.TargetMargin, ClientAreaOnly = clientOnly } };
        }
        selection = selection with { RegionName = selectedInput.Name };
        completed = true;
        using WindowCapturePreview? confirmedPreview = !clientOnly && useWindowSource && roots is null && target is not null && windowPreview?.Target == target
            ? windowPreview : null;
        if (confirmedPreview is not null) windowPreview = null;
        Bitmap? result = null;
        Services.CaptureMetadata? metadata = null;
        try
        {
            CloseOverlays();
            if (clientOnly && (!useWindowSource || options.SelectionOnly))
            {
                if (target is null) throw new InvalidOperationException("撮影するウィンドウを選択してください。");
                Rectangle clientBounds = CaptureNative.RequireClientBounds(target);
                if (!options.SelectionOnly && OutputSettings.FreezeFrame && target.ClientBoundsAtSelection != clientBounds)
                    throw new InvalidOperationException("選択中にウィンドウの描画領域が変わりました。もう一度撮影してください。");
                if (!CaptureGeometry.IsFullyCovered(clientBounds, SelectionMonitors))
                    throw new InvalidOperationException("ウィンドウの描画領域全体が撮影可能なモニター内にありません。");
                selection = selection with { Bounds = clientBounds };
            }
            if (Mode == CaptureMode.Scroll && !entireMonitor && options.AllowScroll)
            {
                Rectangle viewport = selection.Bounds;
                Point center = new(viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2);
                var settings = OutputSettings with { DefaultDestination = Destination };
                var scroll = scrollCollector is null
                    ? await ScrollCapture.CollectAsync(viewport, settings, findWindow?.Invoke(center)?.Handle ?? 0, Snapshot.Context?.FocusedElement)
                    : await scrollCollector(viewport, settings);
                if (scroll is { } capture)
                    completion.TrySetResult(new CaptureOutcome(capture.Image, selection with
                    {
                        ReplaySource = new CaptureReplaySource.Scroll(viewport)
                    }, outputSettings: settings, metadata: capture.Metadata,
                        captureStartedAt: options.StartWasDelayed ? Snapshot.CapturedAt : null));
                else
                    completion.TrySetResult(new CaptureOutcome(null, null));
                return;
            }
            if (OutputSettings.ShareCapturePresentation && captureWindowsKey)
                CaptureNative.SynchronizeDesktop();
            if (!options.SelectionOnly && (IsScreenshot || options.IncludeRecordingImage))
            {
                if (controlGroup is { Isolated: true })
                {
                    var capture = await IsolatedWindowCapture.TakeControlsAsync(controlGroup.Targets,
                        OutputSettings with { DefaultDestination = Destination }, Snapshot.Context?.FocusedElement);
                    result = capture.Image;
                    metadata = capture.Metadata;
                }
                else if (useWindowSource)
                {
                    if (target is null) throw new InvalidOperationException("撮影するウィンドウを選択してください。");
                    var capture = await IsolatedWindowCapture.TakeAsync(target, OutputSettings with { DefaultDestination = Destination },
                        Snapshot.Context?.FocusedElement, confirmedPreview, groupWindows ? group : [target], roots, clientOnly);
                    if (control is not null || selection.TargetMargin is not null)
                    {
                        using var raw = capture.Image;
                        result = CaptureTargetMargin.CropFrame(raw, capture.Bounds, selection.Bounds, control is not null ? 1 : CaptureGeometry.MinimumSize);
                        metadata = capture.Metadata?.WithCrop(selection.Bounds).WithPadding(OutputSettings.ScreenshotPaddingPixels);
                    }
                    else
                    {
                        result = capture.Image;
                        metadata = capture.Metadata;
                        selection = selection with { Bounds = capture.Bounds };
                    }
                }
                using DesktopSnapshot? current = useWindowSource || OutputSettings.FreezeFrame || options.Intent == CaptureIntent.Recording ? null : TakeFrame(OutputSettings);
                DesktopSnapshot frame = current ?? Snapshot;
                if (clientOnly && !useWindowSource && CaptureNative.RequireClientBounds(target!) != selection.Bounds)
                    throw new InvalidOperationException("撮影中にウィンドウの描画領域が変わりました。もう一度撮影してください。");
                if (!useWindowSource && frame.Pointer is { } pointer && OutputSettings.MetadataMode != Services.CaptureMetadataMode.None && Destination.HasFlag(OutputDestination.File))
                {
                    var context = frame.Context ?? await Services.CaptureContextClient.Shared.ObserveAsync(new Point(pointer.Screen.X, pointer.Screen.Y));
                    if (current is not null) context = context with { FocusAtSelectionStart = Snapshot.Context?.FocusedElement };
                    metadata = Services.CaptureMetadata.Create(selection.Bounds, OutputSettings.ScreenshotPaddingPixels, frame.CapturedAt, pointer, context,
                        Mode == CaptureMode.Window && !entireMonitor && context.PointerApplication.Bounds == Services.PixelBounds.From(
                            selection.TargetMargin.HasValue && target is not null ? target.Bounds : selection.Bounds) ? context.PointerApplication : null,
                        selection.Polygon);
                }
                // A display-layout change must not silently shift the chosen physical region.
                if (!useWindowSource && (!frame.Bounds.Contains(selection.Bounds)
                    || targetOffset is > 0 && !CaptureGeometry.IsFullyCovered(selection.Bounds, frame.Monitors)
                    || controlGroup is not null && controlGroup.Targets.Any(hit => !CaptureGeometry.IsFullyCovered(hit.Bounds, frame.Monitors))))
                    throw new InvalidOperationException("選択中にディスプレイ領域が変わりました。もう一度撮影してください。");
                if (controlGroup is null && Mode == CaptureMode.FixedRegion && !entireMonitor && !CaptureGeometry.IsFullyCovered(selection.Bounds, frame.Monitors))
                    throw new InvalidOperationException("選択中にディスプレイ配置が変わりました。もう一度撮影してください。");
                result ??= selection.Composite?.Regions is { } regions
                    ? CaptureGeometry.ComposeRegions(frame.Image, frame.Bounds, selection.Bounds, regions)
                    : selection.Polygon is { } polygon
                    ? CaptureGeometry.Freeform(frame.Image, frame.Bounds, polygon)
                    : CaptureGeometry.Crop(frame.Image, frame.Bounds, selection.Bounds, clientOnly || control is not null || Mode == CaptureMode.FixedRegion ? 1 : CaptureGeometry.MinimumSize);
                if (result is null) { completion.TrySetResult(new CaptureOutcome(null, null)); return; }
            }
            completion.TrySetResult(new CaptureOutcome(result, selection, outputSettings: OutputSettings with { DefaultDestination = Destination },
                metadata: metadata, captureStartedAt: options.StartWasDelayed ? Snapshot.CapturedAt : null));
            result = null;
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { result?.Dispose(); Snapshot.Dispose(); }
    }

    private void RestartAfterDelay(CaptureStartDelay delay)
    {
        if (completed || refreshing || delay == CaptureStartDelay.None) return;

        var current = options with
        {
            Mode = Mode, Destination = Destination, FreezeFrame = OutputSettings.FreezeFrame,
            IncludeCursor = OutputSettings.IncludeCursor, FixedSize = activeFixedSize, FixedAnchor = activeAnchor,
            LimitToStartingMonitor = selectionBounds is not null, Grid20 = Grid20, ShowSelectionGuide = ShowSelectionGuide,
            OutputSettings = OutputSettings, DimCaptureBackground = DimCaptureBackground,
            InitialInputPreset = selectedInput, InitialOutputPreset = selectedOutput,
            RestartState = new(gesture.FixedPosition is not null, exclusiveWindow, AspectRatio, temporaryPadding) { ClientAreaOnly = clientAreaOnly }
        };
        FinishCommand(false, delayedRestart: new(delay, current));
    }

    private void FinishCommand(bool openSettings, bool escapedBeforeSelectionInput = false, CaptureSettingsLink? settingsLink = null,
        DelayedCaptureRestart? delayedRestart = null)
    {
        if (completed) return;
        completed = true;
        try
        {
            CloseOverlays();
            completion.TrySetResult(new CaptureOutcome(null, null, openSettings,
                escapedBeforeSelectionInput: escapedBeforeSelectionInput, settingsLink: settingsLink, delayedRestart: delayedRestart));
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
