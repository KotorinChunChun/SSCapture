using System.Drawing.Imaging;
using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record DisplayTarget(string DeviceName, Rectangle Bounds, bool IsPrimary = false);

internal static class InstantCapture
{
    internal static IReadOnlyList<DisplayTarget> OrderDisplays(IEnumerable<DisplayTarget> displays) => displays
        .Where(display => display.Bounds.Width > 0 && display.Bounds.Height > 0)
        .OrderByDescending(display => display.IsPrimary)
        .ThenBy(display => display.Bounds.Left).ThenBy(display => display.Bounds.Top)
        .ThenBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<DisplayTarget> ReadDisplays() => OrderDisplays(
        Screen.AllScreens.Select(screen => new DisplayTarget(screen.DeviceName, screen.Bounds, screen.Primary)));

    internal static Rectangle ResolveDisplay(int number, IEnumerable<DisplayTarget> displays)
    {
        if (number is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(number));
        var available = OrderDisplays(displays);
        if (number > available.Count)
            throw new InvalidOperationException($"画面{number}は接続されていません（接続中: {available.Count}画面）。");
        return available[number - 1].Bounds;
    }

    // No windows are shown, hidden or activated before reading these physical pixels.
    internal static Bitmap Take(int number, bool includeCursor = false)
    {
        Rectangle bounds = ResolveDisplay(number, ReadDisplays());
        return Take(bounds, includeCursor);
    }

    internal static Bitmap Take(Rectangle bounds, bool includeCursor = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > RegionCoordinates.MaximumDimension
            || bounds.Height > RegionCoordinates.MaximumDimension || (long)bounds.Width * bounds.Height > RegionCoordinates.MaximumPixels
            || (long)bounds.X + bounds.Width > int.MaxValue || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentException("即時撮影の座標または寸法が正しくありません。範囲を確認してください。", nameof(bounds));
        using var cursor = includeCursor ? CursorSnapshot.Capture() : null;
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, DesktopSnapshot.CopyOperation);
            cursor?.Draw(image, bounds.Location);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static async Task<string> DeliverAsync(Bitmap image, AppSettings settings, DateTimeOffset capturedAt, OutputRouter output,
        Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognize = null, string regionName = "撮影", Rectangle? captureBounds = null)
    {
        OutputDestination destination = settings.DefaultDestination & (OutputDestination.Clipboard | OutputDestination.File);
        if (destination == OutputDestination.None) return "即時撮影の出力先がOFFです。クリップボードまたはファイル保存を有効にしてください。";
        string ocr = "", warning = "";
        if (destination.HasFlag(OutputDestination.File) && FileNamePattern.UsesOcr(settings.FileNamePattern))
        {
            OcrResult result;
            try { result = await Task.Run(() => (recognize ?? OneOcr.RecognizeAsync)(image, settings.OneOcrDirectory, CancellationToken.None)); }
            catch (Exception ex) { result = new OcrResult([], ex.Message); }
            ocr = FileNamePattern.CleanOcr(result.Lines.FirstOrDefault() ?? "");
            if (string.IsNullOrWhiteSpace(ocr) || result.Error is not null)
            {
                ocr = "OCRなし";
                warning = "OCRを取得できなかったためファイル名に「OCRなし」を使用しました。" + result.Error + Environment.NewLine;
            }
        }
        var plan = destination.HasFlag(OutputDestination.File) ? new FileNamePlan(settings.FileNamePattern, capturedAt, CaptureIntent.Screenshot, ocr, Region: regionName) : null;
        return warning + await output.ScreenshotAsync(image, destination, settings with { OpenFolderAfterSave = false }, namePlan: plan, captureBounds: captureBounds);
    }
}
