using System.Runtime.InteropServices;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunFrozenBrowserCapture(CaptureWindow target, PixelBounds pixels, string directory)
    {
        var bounds = new Rectangle(pixels.X, pixels.Y, pixels.Width, pixels.Height);
        Point pixel = bounds.Location + new Size(12, 12);
        Point hit = bounds.Location + new Size(bounds.Width / 2, bounds.Height / 2);
        Point[] startCandidates = Screen.AllScreens.SelectMany(screen => new[] {
            screen.WorkingArea.Location + new Size(8, 8),
            new Point(screen.WorkingArea.Right - 8, screen.WorkingArea.Bottom - 8) }).ToArray();
        Point outside = startCandidates.First(point => !CaptureNative.RequireCaptureWindow(target).Contains(point));
        var initial = Color.FromArgb(0x26, 0x64, 0xc2);
        var changed = Color.FromArgb(0xde, 0x76, 0x17);
        using var host = new Form { ShowInTaskbar = false, Opacity = 0 };
        Exception? error = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(async () =>
        {
            SessionController? session = null;
            try
            {
                using var input = new NativeSmokeInput();
                NativeSmokeInput.FocusFixture(target.Handle);
                if (FrozenRootOf(CaptureNative.WindowFromPoint(hit), 2) != target.Handle)
                    throw new InvalidOperationException("実入力の位置にテスト用ブラウザーがありません。");
                input.Move(hit);
                input.LeftDown();
                await Task.Delay(65);
                input.LeftUp();
                await WaitUntil(() => CaptureNative.GetForegroundWindow() == target.Handle, "テスト用ブラウザーを前面にできません。");
                NativeSmokeInput.RequireForeground(target.Handle);
                input.Move(outside);
                Equal(initial.ToArgb(), ReadScreenPixel(pixel));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Task<CaptureOutcome> pending = CaptureSession.SelectAsync(new(CaptureMode.Rectangle,
                    CaptureIntent.Screenshot, OutputDestination.Clipboard, FreezeFrame: true,
                    LimitToStartingMonitor: false, ShowSelectionGuide: false));
                var overlay = await FindOverlay();
                session = OverlaySession(overlay);
                Console.WriteLine($"固定の選択画面表示 {watch.ElapsedMilliseconds}ms");
                Equal(initial.ToArgb(), Pixel(session.Snapshot.Image, session.Snapshot.Bounds, pixel));
                await WaitUntil(() => CaptureContextWorker.ReadApplication(target.Handle).Title?.Contains(" updated", StringComparison.Ordinal) == true,
                    "撮影開始後にブラウザーのボタンの表示を変更できませんでした。");
                input.Move(hit);
                NativeSmokeInput.RequirePointTarget(overlay.Handle, hit);
                input.KeyDown(0xA2);
                await WaitUntil(() => session.ControlBounds == bounds, "ブラウザー外から開始した固定撮影でCtrlのボタン枠を取得できません。");
                var preview = session.PreviewFrame ?? throw new InvalidOperationException("固定プレビューがありません。");
                Equal(initial.ToArgb(), Pixel(preview.Image, preview.Bounds, pixel));
                NativeSmokeInput.RequirePointTarget(overlay.Handle, hit);
                input.LeftDown();
                await Task.Delay(65);
                input.LeftUp();
                using var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(4));
                input.KeyUp(0xA2);
                Equal(bounds, outcome.Selection!.Bounds);
                Equal(bounds.Size, outcome.Image!.Size);
                Equal(initial.ToArgb(), Pixel(outcome.Image, bounds, pixel));
                await WaitUntil(() => ReadScreenPixel(pixel) == changed.ToArgb(), "テストページの現在の画素が意図した色に変わっていません。");
                IDataObject? previous = Clipboard.GetDataObject();
                try
                {
                    using var router = new OutputRouter(Path.Combine(directory, "output"));
                    await router.ScreenshotAsync(outcome.Image, OutputDestination.Clipboard, new AppSettings());
                    using Bitmap copied = Clipboard.GetImage() as Bitmap ?? throw new InvalidOperationException("クリップボードに画像がありません。");
                    Equal(bounds.Size, copied.Size);
                    Equal(initial.ToArgb(), Pixel(copied, bounds, pixel));
                    for (int y = 0; y < copied.Height; y++)
                    for (int x = 0; x < copied.Width; x++)
                        Equal(outcome.Image.GetPixel(x, y).ToArgb(), copied.GetPixel(x, y).ToArgb());
                    Console.WriteLine($"FROZEN BROWSER PASS bounds={bounds} clipboard={copied.Width}x{copied.Height} before={initial.ToArgb():X8} now={changed.ToArgb():X8}");
                }
                finally
                {
                    if (previous is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(previous, copy: true);
                }
            }
            catch (Exception ex) { error = ex; }
            finally { session?.Cancel(); host.Close(); }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;

        async Task<CaptureOverlay> FindOverlay()
        {
            CaptureOverlay? result = null;
            await WaitUntil(() => (result = Application.OpenForms.OfType<CaptureOverlay>()
                .FirstOrDefault(overlay => overlay.MonitorBounds.Contains(hit) && overlay.Visible)) is not null,
                "固定撮影のオーバーレイを表示できません。");
            return result!;
        }

        static int ReadScreenPixel(Point point)
        {
            using Bitmap area = InstantCapture.Take(new Rectangle(point, new Size(2, 2)));
            return area.GetPixel(0, 0).ToArgb();
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint FrozenRootOf(nint window, uint flags);
}
