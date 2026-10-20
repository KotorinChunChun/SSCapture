using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSCapture.Services;

[Flags]
internal enum CaptureMetadataMode { None = 0, PngXmp = 1, Json = 2, Both = 3 }
internal sealed record PixelPoint(int X, int Y)
{
    internal static PixelPoint From(Point p) => new(p.X, p.Y);
}
internal sealed record PixelSize(int Width, int Height);
internal sealed record PixelBounds(int X, int Y, int Width, int Height)
{
    internal static PixelBounds From(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
internal sealed record PointerObservation(DateTimeOffset ObservedAt, PixelPoint Screen, bool Visible, bool Drawn);
internal sealed record PointerMetadata(DateTimeOffset ObservedAt, PixelPoint Screen, PixelPoint RelativeCapture,
    PixelPoint RelativeOutput, bool Visible, bool Drawn, bool InsideCapture);
internal sealed record ElementMetadata(string Status, string? Reason = null, string? AutomationId = null,
    int? ControlType = null, string? Name = null, string? ClassName = null, string? FrameworkId = null,
    PixelBounds? Bounds = null, int? ProcessId = null);
internal sealed record ApplicationMetadata(string Status, string? Reason = null, int? ProcessId = null,
    string? Exe = null, string? Path = null, string? Class = null, string? Title = null, PixelBounds? Bounds = null);
internal sealed record CaptureContext(DateTimeOffset ObservedAt, ElementMetadata FocusedElement, ElementMetadata PointerElement,
    ApplicationMetadata ForegroundApplication, ApplicationMetadata PointerApplication, ElementMetadata? FocusAtSelectionStart = null);
internal sealed record CaptureMetadata(string CaptureId, int SchemaVersion, string AppVersion, DateTimeOffset ImageCapturedAt,
    DateTimeOffset ContextObservedAt, PixelBounds CaptureBounds, PixelSize SourceSize, int PaddingPixels,
    PixelSize OutputSize, PointerMetadata Pointer, ElementMetadata FocusedElement, ElementMetadata PointerElement,
    ApplicationMetadata ForegroundApplication, ApplicationMetadata PointerApplication,
    ElementMetadata? FocusAtSelectionStart, ApplicationMetadata? SelectedWindow)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    internal static CaptureMetadata Create(Rectangle bounds, int padding, DateTimeOffset imageAt, PointerObservation pointer,
        CaptureContext context, ApplicationMetadata? selectedWindow = null)
    {
        int x = checked(pointer.Screen.X - bounds.X), y = checked(pointer.Screen.Y - bounds.Y);
        return new(Guid.NewGuid().ToString("N"), 1, typeof(CaptureMetadata).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            imageAt, context.ObservedAt, PixelBounds.From(bounds), new(bounds.Width, bounds.Height), padding,
            new(checked(bounds.Width + 2 * padding), checked(bounds.Height + 2 * padding)),
            new(pointer.ObservedAt, pointer.Screen, new(x, y), new(checked(x + padding), checked(y + padding)),
                pointer.Visible, pointer.Drawn, bounds.Contains(pointer.Screen.X, pointer.Screen.Y)),
            context.FocusedElement, context.PointerElement, context.ForegroundApplication, context.PointerApplication,
            context.FocusAtSelectionStart, selectedWindow);
    }
    internal CaptureMetadata WithPadding(int padding) => this with { PaddingPixels = padding,
        OutputSize = new(checked(SourceSize.Width + padding * 2), checked(SourceSize.Height + padding * 2)),
        Pointer = Pointer with { RelativeOutput = new(checked(Pointer.RelativeCapture.X + padding), checked(Pointer.RelativeCapture.Y + padding)) } };
}
