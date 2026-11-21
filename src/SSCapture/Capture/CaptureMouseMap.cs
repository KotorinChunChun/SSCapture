namespace SSCapture.Capture;

internal sealed record CaptureMouseMap(
    bool BeforeLeft = true,
    bool BeforeRight = true,
    bool BeforeMiddle = true,
    bool DragRight = true);
