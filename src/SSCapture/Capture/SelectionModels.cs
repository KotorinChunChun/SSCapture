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
    bool Grid20 = false);

internal sealed record CaptureSelection(
    Rectangle Bounds,
    IReadOnlyList<Point>? Polygon,
    OutputDestination Destination);

internal sealed class CaptureOutcome(Bitmap? image, CaptureSelection? selection, bool openSettings = false) : IDisposable
{
    public Bitmap? Image { get; } = image;
    public CaptureSelection? Selection { get; } = selection;
    public bool OpenSettings { get; } = openSettings;
    public void Dispose() => Image?.Dispose();
}
