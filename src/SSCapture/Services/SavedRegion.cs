namespace SSCapture.Services;

internal sealed record DisplayLayout(string Identity, string Name, Rectangle Bounds, int DpiX, int DpiY);

internal sealed record SavedRegion
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "範囲";
    public string Shortcut { get; init; } = "";
    public Rectangle Bounds { get; init; }
    public string? AnchorIdentity { get; init; }
    public Rectangle RelativeBounds { get; init; }
    public DisplayLayout[] Layout { get; init; } = [];

    internal void Validate() => RegionCoordinates.ValidateSaved(this);
}
