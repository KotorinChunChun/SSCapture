using System.Runtime.InteropServices;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunFrozenBrowserCapture(CaptureWindow target, PixelBounds pixels, string directory, string? textKind = null)
    {
        var bounds = new Rectangle(pixels.X, pixels.Y, pixels.Width, pixels.Height);

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
                if (textKind is null || CaptureNative.GetForegroundWindow() != target.Handle) await ClickBrowser();
                await WaitUntil(() => CaptureNative.GetForegroundWindow() == target.Handle, "テスト用ブラウザーを前面にできません。");
                NativeSmokeInput.RequireForeground(target.Handle);
                input.Move(outside);
                Check(ScreenHasColor(bounds, initial), "開始前の対象に検証色がありません。");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Task<CaptureOutcome> pending = CaptureSession.SelectAsync(new(CaptureMode.Rectangle,
                    CaptureIntent.Screenshot, OutputDestination.Clipboard, FreezeFrame: true,
                    LimitToStartingMonitor: false, ShowSelectionGuide: false));
                var overlay = await FindOverlay();
                session = OverlaySession(overlay);
                Console.WriteLine($"固定の選択画面表示 {watch.ElapsedMilliseconds}ms");
                using var source = session.Snapshot.Image.Clone(new Rectangle(bounds.Location - new Size(session.Snapshot.Bounds.Location), bounds.Size),
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Check(HasColor(source, initial), "開始時の固定画像に検証色がありません。");
                if (textKind is not null)
                {
                    await session.Snapshot.FrozenControlsReady;
                    File.WriteAllText(Path.Combine(directory, "change-target.js"), "changeFixtureColor();");
                }
                await WaitUntil(() => HasTitleMarker(" updated"), "撮影開始後にページの対象色が変わりません。");
                input.Move(hit);
                NativeSmokeInput.RequirePointTarget(overlay.Handle, hit);
                input.KeyDown(0xA2);
                await session.Snapshot.FrozenControlsReady;
                await WaitUntil(() => session.SelectingControl && session.Pointer == hit, "Ctrlの実入力を確認できません。");
                Rectangle selected = session.ControlBounds;
                if (textKind is not null)
                {
                    var frozen = session.Snapshot.FrozenTargets;
                    var window = frozen?.SelectableWindows.FirstOrDefault(window => window.Handle == target.Handle);
                    var layout = window is null ? null : frozen!.ControlLayout(window, session.Snapshot.Monitors);
                    Console.WriteLine($"{textKind} after verification: {layout?.Entries.Length} candidates; at pointer:");
                    if (layout is not null) PrintTextFixtureCandidates(layout, hit);
                    Console.WriteLine($"{textKind} expected={bounds} selected={selected}");
                }
                var preview = session.PreviewFrame;
                if (preview is not null && preview.Bounds.Contains(bounds))
                    VerifyPixels(source, preview.Image, bounds.Location - new Size(preview.Bounds.Location));
                NativeSmokeInput.RequirePointTarget(overlay.Handle, hit);
                input.LeftDown();
                await WaitUntil(() => session.HasPointerPress, "選択画面に左ボタンの押下が届きません。");
                input.LeftUp();
                using var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(4));
                input.KeyUp(0xA2);
                var captured = outcome.Selection!.Bounds;
                Console.WriteLine($"{textKind ?? "Chrome"} captured={captured}");
                Equal(bounds, selected);
                Equal(bounds, captured);
                Equal(bounds.Size, outcome.Image!.Size);
                Check(preview is not null && preview.Bounds.Contains(bounds), "対象全体の固定プレビューがありません。");
                VerifyPixels(source, outcome.Image, Point.Empty);
                await WaitUntil(() => ScreenHasColor(bounds, changed), "テストページの現在の画素が意図した色に変わっていません。");
                DataObject? previous = SnapshotClipboard();
                try
                {
                    using var router = new OutputRouter(Path.Combine(directory, "output"));
                    await router.ScreenshotAsync(outcome.Image, OutputDestination.Clipboard, new AppSettings());
                    using Bitmap copied = Clipboard.GetImage() as Bitmap ?? throw new InvalidOperationException("クリップボードに画像がありません。");
                    Equal(bounds.Size, copied.Size);

                    VerifyPixels(source, copied, Point.Empty);
                    Console.WriteLine($"FROZEN BROWSER PASS bounds={bounds} clipboard={copied.Width}x{copied.Height} before={initial.ToArgb():X8} now={changed.ToArgb():X8}");
                }
                finally
                {
                    if (previous is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(previous, copy: true);
                }

                async Task ClickBrowser()
                {
                    if (FrozenRootOf(CaptureNative.WindowFromPoint(hit), 2) != target.Handle)
                        throw new InvalidOperationException("クリック位置が検証用ブラウザーにありません。");
                    input.LeftDown();
                    await Task.Delay(65);
                    input.LeftUp();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{textKind ?? "Chrome"} click result: {session?.SelectionError ?? "選択エラー表示なし"}");
                error = ex;
            }
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

        bool HasTitleMarker(string marker) =>
            CaptureContextWorker.ReadApplication(target.Handle).Title?.Contains(marker, StringComparison.Ordinal) == true;

        static void VerifyPixels(Bitmap expected, Bitmap actual, Point offset)
        {
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x + offset.X, y + offset.Y).ToArgb());
        }

        // Detach the existing formats before OLE clipboard ownership changes.
        static DataObject? SnapshotClipboard()
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return null;
            var copy = new DataObject();
            foreach (string format in current.GetFormats(autoConvert: false))
            {
                object? data = current.GetData(format, autoConvert: false);
                object preserved = data switch
                {
                    string text => text,
                    string[] files => files.ToArray(),
                    byte[] bytes => bytes.ToArray(),
                    MemoryStream stream => new MemoryStream(stream.ToArray()),
                    Image image => new Bitmap(image),
                    _ => throw new InvalidOperationException($"退避できないクリップボード形式があります（{format}）。")
                };
                copy.SetData(format, autoConvert: false, preserved);
            }
            return copy;
        }

        static bool ScreenHasColor(Rectangle bounds, Color color)
        {
            using Bitmap area = InstantCapture.Take(bounds);
            return HasColor(area, color);
        }

        static bool HasColor(Bitmap area, Color color)
        {
            int argb = color.ToArgb();
            for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
                if (area.GetPixel(x, y).ToArgb() == argb) return true;
            return false;
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint FrozenRootOf(nint window, uint flags);
}
