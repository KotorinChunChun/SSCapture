using SSCapture.Capture;

namespace SSCapture.Services;

// 画像履歴とは独立した、成功した静止画の入出力条件だけの記録。
internal sealed class LastCaptureRecipe
{
    private readonly AppSettings settings;
    private readonly SavedRegion? region;
    internal long Sequence { get; }
    internal Rectangle Bounds { get; }
    internal CaptureWindow? IsolatedWindow { get; }
    internal IReadOnlyList<Point>? Polygon { get; }
    internal AppSettings Settings => CaptureSettingsSnapshot.Copy(settings);

    private LastCaptureRecipe(long sequence, Rectangle bounds, SavedRegion? region, IReadOnlyList<Point>? polygon, AppSettings settings,
        CaptureWindow? isolatedWindow)
    {
        Sequence = sequence;
        this.region = region;
        Bounds = bounds;
        IsolatedWindow = isolatedWindow;
        Polygon = polygon is null ? null : Array.AsReadOnly(polygon.ToArray());
        this.settings = CaptureSettingsSnapshot.Copy(settings);
    }

    internal static LastCaptureRecipe Create(long sequence, Rectangle bounds, IReadOnlyList<Point>? polygon, AppSettings settings,
        IReadOnlyList<DisplayLayout> displays, CaptureWindow? isolatedWindow = null) =>
        new(sequence, bounds, isolatedWindow is null ? RegionCoordinates.FromBounds("直前の撮影", bounds, displays) : null,
            polygon, settings, isolatedWindow);

    internal Rectangle Resolve(IReadOnlyList<DisplayLayout> displays) => region is null
        ? throw new InvalidOperationException("ウィンドウ単独撮影は選択ウィンドウから再取得してください。")
        : RegionCoordinates.Resolve(region, displays);

    internal AppSettings SettingsForReplay(AppSettings current) => CaptureSettingsSnapshot.ForReplay(settings, current);

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
}
