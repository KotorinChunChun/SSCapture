namespace SSCapture.Capture;

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
    bool DimCaptureBackground = false);

internal sealed record CaptureSelection(
    Rectangle Bounds,
    IReadOnlyList<Point>? Polygon,
    OutputDestination Destination);

internal sealed class CaptureOutcome(Bitmap? image, CaptureSelection? selection, bool openSettings = false,
    SSCapture.Services.AppSettings? outputSettings = null) : IDisposable
{
    public Bitmap? Image { get; } = image;
    public CaptureSelection? Selection { get; } = selection;
    public bool OpenSettings { get; } = openSettings;
    public SSCapture.Services.AppSettings? OutputSettings { get; } = outputSettings;
    public void Dispose() => Image?.Dispose();
}
