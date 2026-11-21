namespace SSCapture.Capture;

internal enum CaptureSelectionMethod { Drag, TwoClicks }

internal sealed record CaptureOptions(
    CaptureMode Mode,
    CaptureIntent Intent,
    OutputDestination Destination,
    bool FreezeFrame = true,
    bool IncludeRecordingImage = false,
    bool IncludeCursor = false,
    Size? FixedSize = null,
    CaptureAnchor FixedAnchor = CaptureAnchor.TopLeft,
    bool LimitToStartingMonitor = true,
    bool Grid20 = false,
    bool ShowSelectionGuide = true,
    SSCapture.Services.AppSettings? OutputSettings = null,
    bool DimCaptureBackground = false,
    SSCapture.Services.SavedRegion? InitialInputPreset = null,
    SSCapture.Services.CapturePreset? InitialOutputPreset = null)
{
    internal bool SelectionOnly { get; init; }
    internal static CaptureOptions ForInputSelection(SSCapture.Services.AppSettings settings, Size? size, CaptureAnchor anchor) => new(
        size is null ? CaptureMode.Rectangle : CaptureMode.FixedRegion, CaptureIntent.Recording, OutputDestination.None,
        FixedSize: size, FixedAnchor: anchor, LimitToStartingMonitor: settings.LimitToStartingMonitor,
        Grid20: settings.Grid20, ShowSelectionGuide: settings.ShowSelectionGuide, OutputSettings: settings,
        DimCaptureBackground: settings.DimCaptureBackground);
}

internal sealed record CaptureSelection(
    Rectangle Bounds,
    IReadOnlyList<Point>? Polygon,
    OutputDestination Destination,
    Point? WindowHitPoint = null,
    string? WindowTitle = null,
    CaptureComposite? Composite = null,
    CaptureReplaySource? ReplaySource = null,
    CaptureReplaySource.Control? ControlSource = null,
    string? RegionName = null);

internal enum CaptureSettingsDestination { General, InputPreset, OutputPreset, Shortcut }

internal sealed record CaptureSettingsLink(CaptureSettingsDestination Destination, string? PresetId = null,
    string? ShortcutControlName = null);

internal sealed class CaptureOutcome(Bitmap? image, CaptureSelection? selection, bool openSettings = false,
    SSCapture.Services.AppSettings? outputSettings = null, SSCapture.Services.CaptureMetadata? metadata = null,
    bool escapedBeforeSelectionInput = false, CaptureSettingsLink? settingsLink = null) : IDisposable
{
    public Bitmap? Image { get; } = image;
    public CaptureSelection? Selection { get; } = selection;
    public bool OpenSettings { get; } = openSettings;
    internal CaptureSettingsLink? SettingsLink { get; } = settingsLink;
    public bool EscapedBeforeSelectionInput { get; } = escapedBeforeSelectionInput;
    public SSCapture.Services.AppSettings? OutputSettings { get; } = outputSettings;
    public SSCapture.Services.CaptureMetadata? Metadata { get; } = metadata;
    public void Dispose() => Image?.Dispose();
}
