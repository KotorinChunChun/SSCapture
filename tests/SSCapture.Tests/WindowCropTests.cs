using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void WindowCropTests()
    {
        Run("単一窓は四辺を切り抜いてから余白・減色し、各出力で範囲を共有", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var bounds = new Rectangle(-30, 40, 9, 7);
                var crop = new WindowCrop(1, 2, 3, 1);
                var expected = new Rectangle(-27, 41, 5, 4);
                var settings = OutputSettings(directory) with
                {
                    WindowCrop = crop, ScreenshotPaddingPixels = 1, ScreenshotColorDepth = ScreenshotColorDepth.Indexed4,
                    MetadataMode = CaptureMetadataMode.Json, NotificationDurationSeconds = 5
                };
                using var image = new Bitmap(9, 7);
                using (var g = Graphics.FromImage(image)) g.Clear(Color.Magenta);
                image.SetPixel(3, 1, Color.Black);
                CaptureDelivery? delivery = null;
                LastCaptureRecipe? recipe = null;
                var source = new CaptureReplaySource.Windows([new(bounds, "窓", 1, 2, 3)], true, true, true);
                using var router = new OutputRouter(Path.Combine(directory, "work"), bitmap =>
                {
                    Equal(new Size(7, 6), bitmap.Size);
                    Equal(Color.White.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
                    Equal(Color.Black.ToArgb(), bitmap.GetPixel(1, 1).ToArgb());
                    return Task.CompletedTask;
                }, delivered: result => delivery = result, captureSucceeded: (_, result) => recipe = result,
                pinImage: (_, pinBounds, _) => Equal(expected, pinBounds),
                capturedWithBounds: (_, _, _, historyBounds) => Equal<Rectangle?>(expected, historyBounds));

                var time = DateTimeOffset.Now;
                var element = new ElementMetadata("ok", Bounds: new(-27, 41, 1, 1));
                var app = new ApplicationMetadata("ok");
                var metadata = CaptureMetadata.Create(bounds, 0, time, new(time, new(-28, 40), true, true),
                    new(time, element, element, app, app));
                await router.ScreenshotAsync(image, OutputDestination.File | OutputDestination.Clipboard | OutputDestination.Pinned,
                    settings, captureBounds: bounds, replaySource: source, windowTitle: "窓", metadata: metadata,
                    requestFileNameForImage: naming =>
                    {
                        Equal(expected.Size, naming.Size);
                        Equal(Color.Black.ToArgb(), naming.GetPixel(0, 0).ToArgb());
                        return Task.FromResult<ScreenshotFileName?>(new("crop.png", null, false));
                    });
                using var pending = delivery!.NotificationImage;
                Equal<Rectangle?>(expected, delivery.CaptureBounds);
                Check(delivery.Message.Contains("-27,41 / 5×4px"), "通知の寸法または原点が一致しない");
                var json = System.Text.Json.JsonSerializer.Deserialize<CaptureMetadata>(
                    File.ReadAllText(Path.ChangeExtension(delivery.SavedPath!, ".json")), CaptureMetadata.JsonOptions)!;
                Equal(PixelBounds.From(expected), json.CaptureBounds);
                Equal(new PixelPoint(-1, -1), json.Pointer.RelativeCapture);
                Equal(new PixelPoint(0, 0), json.Pointer.RelativeOutput);
                Check(!json.Pointer.InsideCapture && !json.Pointer.Drawn, "切り抜いたポインタが画像内に残った");
                Equal(new PixelBounds(1, 1, 1, 1), json.FocusedElement.RelativeOutput);
                Equal(new PixelSize(7, 6), json.OutputSize);
                Check(recipe!.IsSingleWindowSelection && recipe.IncludeOwnedWindows && recipe.UseIsolatedWindow, "選択属性を失った");
                Equal(bounds, recipe.Bounds);
                Equal(crop, recipe.Settings.WindowCrop);
            })));

        Run("WGCの合成外周を基準とし、手動ウィンドウ合成は元寸法を維持", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = new Bitmap(6, 4);
                image.SetPixel(5, 1, Color.Red);
                var root = new CaptureWindow(new(30, 20, 4, 4), "窓", 1, 2, 3);
                var bounds = new Rectangle(30, 20, 6, 4);
                Size size = default;
                using var router = new OutputRouter(directory, bitmap =>
                {
                    size = bitmap.Size;
                    Equal(Color.Red.ToArgb(), bitmap.GetPixel(5, bitmap.Height == 3 ? 0 : 1).ToArgb());
                    return Task.CompletedTask;
                });
                var settings = OutputSettings(directory) with { WindowCrop = new(1, 0, 0, 0) };
                await router.ScreenshotAsync(image, OutputDestination.Clipboard, settings, captureBounds: bounds,
                    replaySource: new CaptureReplaySource.Windows([root], true, true, true));
                Equal(new Size(6, 3), size);
                await router.ScreenshotAsync(image, OutputDestination.Clipboard, settings, captureBounds: bounds,
                    replaySource: new CaptureReplaySource.Windows([root], true, true));
                Equal(image.Size, size);
            })));

        Run("四辺0は全画素、残り1pxは有効、0px以下は該当軸を案内", () =>
        {
            using var image = new Bitmap(4, 3);
            using var same = default(WindowCrop).Apply(image);
            Equal(image.Size, same.Size);
            using var one = new WindowCrop(1, 1, 2, 1).Apply(image);
            Equal(new Size(1, 1), one.Size);
            foreach (var (crop, axis) in new[] { (new WindowCrop(0, 0, int.MaxValue, int.MaxValue), "左右"), (new WindowCrop(2, 1, 0, 0), "上下") })
            {
                bool rejected = false;
                try { using var invalid = crop.Apply(image); }
                catch (ArgumentException ex) { rejected = ex.Message.Contains(axis); }
                Check(rejected, "不正な辺を受理した");
            }
            var preset = System.Text.Json.JsonSerializer.Deserialize<CapturePreset>("{\"Name\":\"旧設定\"}")!;
            Equal(default(WindowCrop), preset.WindowCrop);
        });
    }
}
