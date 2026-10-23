using SSCapture.Capture;

namespace SSCapture.Services;

// 画像履歴とは独立した、成功した静止画の入出力条件だけの記録。
internal sealed class LastCaptureRecipe
{
    readonly AppSettings settings;
    readonly SavedRegion region;
    internal long Sequence { get; }
    internal Rectangle Bounds => region.Bounds;
    internal IReadOnlyList<Point>? Polygon { get; }
    internal AppSettings Settings => Snapshot(settings);
    private LastCaptureRecipe(long sequence, SavedRegion region, IReadOnlyList<Point>? polygon, AppSettings settings)
    { Sequence = sequence; this.region = region; Polygon = polygon is null ? null : Array.AsReadOnly(polygon.ToArray()); this.settings = Snapshot(settings); }
    internal static LastCaptureRecipe Create(long sequence, Rectangle bounds, IReadOnlyList<Point>? polygon, AppSettings settings, IReadOnlyList<DisplayLayout> displays) =>
        new(sequence, RegionCoordinates.FromBounds("直前の撮影", bounds, displays), polygon, settings);
    internal Rectangle Resolve(IReadOnlyList<DisplayLayout> displays) => RegionCoordinates.Resolve(region, displays);
    internal void ValidateApplications(AppSettings current)
    {
        if (!settings.DefaultDestination.HasFlag(OutputDestination.ExternalApp)) return;
        string id = settings.SelectedImageEditorId;
        var original = settings.ImageEditors.SingleOrDefault(a => a.Id == id);
        var now = current.ImageEditors.SingleOrDefault(a => a.Id == id);
        if (original is null || now != original) throw new InvalidOperationException("直前の外部アプリが削除・変更されています。通常撮影で設定を確認してください。");
    }
    internal IReadOnlyList<Point>? PolygonFor(Rectangle bounds) => Polygon?.Select(p => new Point(checked(p.X + bounds.X - Bounds.X), checked(p.Y + bounds.Y - Bounds.Y))).ToArray();
    internal Bitmap Shape(Bitmap image) => Polygon is null ? (Bitmap)image.Clone() :
        CaptureGeometry.Freeform(image, Bounds, Polygon) ?? throw new InvalidOperationException("直前の自由形状を再現できません。");
    private static AppSettings Snapshot(AppSettings value) => value with
    {
        SavedRegions = value.SavedRegions.Select(p => p with { Layout = p.Layout.ToArray() }).ToArray(),
        CapturePresets = value.CapturePresets.ToArray(), ImageEditors = value.ImageEditors.ToArray(), VideoEditors = value.VideoEditors.ToArray(),
        InstantShortcuts = value.InstantShortcuts.ToArray()
    };
}

internal sealed class CaptureRecipeBook
{
    readonly object gate = new();
    long sequence;
    LastCaptureRecipe? current;
    internal LastCaptureRecipe? Current { get { lock (gate) return current; } }
    internal void Remember(long generation, LastCaptureRecipe? recipe)
    { lock (gate) { if (generation <= sequence) return; sequence = generation; current = recipe; } }
}
