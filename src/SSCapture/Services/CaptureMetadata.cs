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
    PixelPoint RelativeOutput, bool Visible, bool Drawn, bool InsideCapture, bool? InsidePolygon = null);
internal sealed record ElementMetadata(string Status, string? Reason = null, string? AutomationId = null,
    int? ControlType = null, string? Name = null, string? ClassName = null, string? FrameworkId = null,
    PixelBounds? Bounds = null, int? ProcessId = null, DateTimeOffset? ObservedAt = null,
    PixelBounds? RelativeCapture = null, PixelBounds? RelativeOutput = null, bool? InsideCapture = null);
internal sealed record ApplicationMetadata(string Status, string? Reason = null, int? ProcessId = null,
    string? Exe = null, string? Path = null, string? Class = null, string? Title = null, PixelBounds? Bounds = null);
internal sealed record CaptureContext(DateTimeOffset ObservedAt, ElementMetadata FocusedElement, ElementMetadata PointerElement,
    ApplicationMetadata ForegroundApplication, ApplicationMetadata PointerApplication, ElementMetadata? FocusAtSelectionStart = null,
    bool ForegroundChanged = false, DateTimeOffset? CompletedAt = null);
internal sealed record CaptureMetadata(string CaptureId, int SchemaVersion, string AppVersion, DateTimeOffset ImageCapturedAt,
    DateTimeOffset ContextObservedAt, PixelBounds CaptureBounds, PixelSize SourceSize, int PaddingPixels,
    PixelSize OutputSize, PointerMetadata Pointer, ElementMetadata FocusedElement, ElementMetadata PointerElement,
    ApplicationMetadata ForegroundApplication, ApplicationMetadata PointerApplication,
    ElementMetadata? FocusAtSelectionStart, ApplicationMetadata? SelectedWindow, string CollectionStatus = "success",
    string[]? MissingFields = null, DateTimeOffset? ContextCompletedAt = null, bool ForegroundChanged = false)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    // 撮影中にFileをONへ切り替えられるため、出力フラグではなく記録方式の有無で準備する。
    internal static bool RequiresPreparation(AppSettings settings) => settings.MetadataMode != CaptureMetadataMode.None ||
        settings.CapturePresets.Any(p => p.MetadataMode != CaptureMetadataMode.None);

    internal static CaptureMetadata Create(Rectangle bounds, int padding, DateTimeOffset imageAt, PointerObservation pointer,
        CaptureContext context, ApplicationMetadata? selectedWindow = null, IReadOnlyList<Point>? polygon = null)
    {
        int x = checked(pointer.Screen.X - bounds.X), y = checked(pointer.Screen.Y - bounds.Y);
        string[] missing = new[] { ("focusedElement", context.FocusedElement.Status), ("pointerElement", context.PointerElement.Status),
            ("foregroundApplication", context.ForegroundApplication.Status), ("pointerApplication", context.PointerApplication.Status) }
            .Where(p => p.Status != "ok").Select(p => p.Item1).ToArray();
        bool inside = bounds.Contains(pointer.Screen.X, pointer.Screen.Y);
        bool? inPolygon = polygon is null ? null : Contains(polygon, pointer.Screen);
        return new(Guid.NewGuid().ToString("N"), 1, typeof(CaptureMetadata).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            imageAt, context.ObservedAt, PixelBounds.From(bounds), new(bounds.Width, bounds.Height), padding,
            new(checked(bounds.Width + 2 * padding), checked(bounds.Height + 2 * padding)),
            new(pointer.ObservedAt, pointer.Screen, new(x, y), new(checked(x + padding), checked(y + padding)),
                pointer.Visible, pointer.Drawn && inside && inPolygon != false, inside, inPolygon),
            Relative(context.FocusedElement, bounds, padding, context.ObservedAt), Relative(context.PointerElement, bounds, padding, context.ObservedAt), context.ForegroundApplication, context.PointerApplication,
            context.FocusAtSelectionStart is null ? null : Relative(context.FocusAtSelectionStart, bounds, padding, context.ObservedAt), selectedWindow,
            missing.Length == 0 && !context.ForegroundChanged ? "success" : "partial", missing, context.CompletedAt, context.ForegroundChanged);
    }
    internal CaptureMetadata WithPadding(int padding) => this with { PaddingPixels = padding,
        OutputSize = new(checked(SourceSize.Width + padding * 2), checked(SourceSize.Height + padding * 2)),
        Pointer = Pointer with { RelativeOutput = new(checked(Pointer.RelativeCapture.X + padding), checked(Pointer.RelativeCapture.Y + padding)) },
        FocusedElement = Relative(FocusedElement, new(CaptureBounds.X, CaptureBounds.Y, CaptureBounds.Width, CaptureBounds.Height), padding, ContextObservedAt),
        PointerElement = Relative(PointerElement, new(CaptureBounds.X, CaptureBounds.Y, CaptureBounds.Width, CaptureBounds.Height), padding, ContextObservedAt),
        FocusAtSelectionStart = FocusAtSelectionStart is null ? null : Relative(FocusAtSelectionStart, new(CaptureBounds.X, CaptureBounds.Y, CaptureBounds.Width, CaptureBounds.Height), padding, ContextObservedAt) };

    private static ElementMetadata Relative(ElementMetadata element, Rectangle capture, int padding, DateTimeOffset at)
    {
        if (element.Bounds is not { } b) return element with { ObservedAt = element.ObservedAt ?? at };
        var relative = new PixelBounds(checked(b.X - capture.X), checked(b.Y - capture.Y), b.Width, b.Height);
        return element with { ObservedAt = element.ObservedAt ?? at, RelativeCapture = relative,
            RelativeOutput = relative with { X = checked(relative.X + padding), Y = checked(relative.Y + padding) },
            InsideCapture = capture.Contains(new Rectangle(b.X, b.Y, b.Width, b.Height)) };
    }
    private static bool Contains(IReadOnlyList<Point> polygon, PixelPoint point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Point a = polygon[i], b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (double)(b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
