using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void PresetConsolidationTests()
    {
        Run("スナップ距離と刻み幅は20既定・独立した物理px", () =>
        {
            Equal(20, new AppSettings().SnapDistancePixels); Equal(20, new AppSettings().GridStepPixels);
            var snap = new SelectionSnap([], [new(100, 100, 100, 100)], 5);
            Equal(new Point(100, 130), snap.Apply(new(105, 130))); Equal(new Point(106, 130), snap.Apply(new(106, 130)));
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-200, -200, 600, 600)], []) { Grid20 = true };
            gesture.SetSnapPrecision(5, 7); gesture.Begin(new(-100, -100), new(1, 1)); gesture.Move(new(-83, -83));
            Equal(new Size(14, 14), gesture.Selection.Size);
            gesture.Move(new(-110, -110)); Equal(new Size(7, 7), gesture.Selection.Size);
            gesture.ExpandMonitors([new(-200, -200, 700, 700)]); Equal(5, gesture.SnapDistancePixels); Equal(7, gesture.GridStepPixels);
        });
        Run("extは静止画・録画・OCR省略・連番で重複拡張子を付けない", () =>
        {
            var at = new DateTimeOffset(2026, 10, 22, 12, 34, 56, TimeSpan.Zero);
            Equal("20261022_123456.png", FileNamePattern.Expand(FileNamePattern.Default, at, CaptureIntent.Screenshot));
            Equal("20261022_123456.mp4", FileNamePattern.Expand(FileNamePattern.Default, at, CaptureIntent.Recording));
            Equal("sample.png", FileNamePattern.Expand("sample_{ocr}.{ext}", at, CaptureIntent.Screenshot));
            Equal("sample_02.mp4", FileNamePattern.Expand("sample_{##}.{ext}", at, CaptureIntent.Recording, sequence: 2));
            Equal("legacy.mp4", FileNamePattern.Expand("legacy.png", at, CaptureIntent.Recording));
        });
        Run("スナップ精度の不正値を拒否し次の撮影へ一時値を漏らさない", () =>
        {
            foreach (int value in new[] { 0, -1, 32769 })
            {
                try { new AppSettings { GridStepPixels = value }.Validate(); throw new Exception("刻み幅を受理した"); } catch (ArgumentException) { }
                try { new SelectionSnap([], [], value); throw new Exception("吸着距離を受理した"); } catch (ArgumentException) { }
            }
            var settings = new AppSettings(); var session = MenuSession(settings: settings);
            try { session.ChangeSnapPrecision(3, 9); Equal(3, session.OutputSettings.SnapDistancePixels); Equal(20, settings.SnapDistancePixels); }
            finally { session.Cancel(); }
        });
    }
}
