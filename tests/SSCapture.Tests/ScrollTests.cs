using System.Drawing.Imaging;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void ScrollTests()
    {
        Run("縦スクロール撮影は重複を除いて先頭・継ぎ目・末尾の全行を保つ", () =>
        {
            var stitch = new ScrollFrameStitcher(new(24, 20), 3);
            void Add(int first, ScrollFrameResult expected)
            {
                using var frame = ScrollRows(24, 20, first);
                Equal(expected, stitch.Add(frame));
            }
            Add(0, ScrollFrameResult.First);
            Add(0, ScrollFrameResult.Unchanged);
            Add(6, ScrollFrameResult.Appended);
            Add(6, ScrollFrameResult.Unchanged);
            Add(50, ScrollFrameResult.Unmatched);
            Add(14, ScrollFrameResult.Appended);
            using var image = stitch.Build();
            Equal(new Size(24, 34), image.Size);
            using var expected = ScrollRows(24, 34, 0);
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    Equal(expected.GetPixel(x, y), image.GetPixel(x, y));
        });
        Run("規則的な反復で合う位置が複数ある場合は継ぎ足さない", () =>
        {
            var stitch = new ScrollFrameStitcher(new(16, 25), 0);
            using var a = ScrollRows(16, 25, 0, period: 3);
            using var b = ScrollRows(16, 25, 1, period: 3);
            Equal(ScrollFrameResult.First, stitch.Add(a));
            Equal(ScrollFrameResult.Unmatched, stitch.Add(b));
            Equal(25, stitch.Height);
        });
        Run("スクロール撮影は余白を含む辺長と総画素数の上限を検証する", () =>
        {
            CaptureCommandReject(() => new ScrollFrameStitcher(new(32767, 24), 1));
            CaptureCommandReject(() => new ScrollFrameStitcher(new(10000, 10000), 1));
            var stitch = new ScrollFrameStitcher(new(1, 32740), 4);
            using var first = ScrollRows(1, 32740, 0);
            using var boundary = ScrollRows(1, 32740, 20);
            using var beyond = ScrollRows(1, 32740, 21);
            Equal(ScrollFrameResult.First, stitch.Add(first));
            Equal(ScrollFrameResult.Appended, stitch.Add(boundary));
            Equal(ScrollFrameResult.Limit, stitch.Add(beyond));
            Equal(32760, stitch.Height);
        });
        Run("スクロールモードは範囲を囲って確定し収集の取消は成果物を返さない", () =>
        {
            Rectangle monitor = new(-600, -30, 600, 300);
            Rectangle viewport = new(-580, 20, 120, 80);
            foreach (bool accept in new[] { false, true })
            {
                using var snapshot = new DesktopSnapshot(new Bitmap(600, 300), monitor, [monitor]);
                int collections = 0;
                var session = new SessionController(new(CaptureMode.Scroll, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    ShowSelectionGuide: false, OutputSettings: new AppSettings { ScreenshotPaddingPixels = 5, CaptureKeys = new(), CaptureBeforeKeys = new() }),
                    snapshot, [monitor], initialPointer: viewport.Location, scrollCollector: (bounds, settings) =>
                    {
                        collections++;
                        Equal(viewport, bounds);
                        Equal(5, settings.ScreenshotPaddingPixels);
                        return Task.FromResult<(Bitmap, CaptureMetadata?)?>(accept ? (new Bitmap(120, 100), null) : null);
                    });
                using var overlay = new CaptureOverlay(session, monitor);
                _ = overlay.Handle;
                try
                {
                    session.MouseDown(overlay, MouseButtons.Left, viewport.Location);
                    session.MouseUp(MouseButtons.Left, viewport.Location);
                    Equal(0, collections);
                    session.MouseDown(overlay, MouseButtons.Left, viewport.Location);
                    session.MoveTo(new Point(viewport.Right, viewport.Bottom));
                    session.MouseUp(MouseButtons.Left, new Point(viewport.Right, viewport.Bottom));
                    using var outcome = session.Completion.GetAwaiter().GetResult();
                    Equal(1, collections);
                    if (accept)
                    {
                        Equal(new Size(120, 100), outcome.Image!.Size);
                        Equal(viewport, outcome.Selection!.Bounds);
                        Equal(viewport, ((CaptureReplaySource.Scroll)outcome.Selection.ReplaySource!).Viewport);
                    }
                    else Check(outcome.Image is null && outcome.Selection is null, "取り消した撮影に出力データがある");
                }
                finally { session.Cancel(); }
            }
        });
        Run("スクロール撮影をCLIで選択し、予約と録画は手動合成に入らない", () =>
        {
            var settings = new AppSettings().EnsurePresets();
            var preset = settings.SavedRegions.Single(p => p.IsDefault) with { Mode = CaptureMode.Scroll };
            settings = settings with { SavedRegions = settings.SavedRegions.Select(p => p.Id == preset.Id ? preset : p).ToArray() };
            var command = CaptureCommandLine.Resolve(new(InputPresetId: preset.Id) { UsePresetMode = true }, settings);
            Equal(CaptureMode.Scroll, command.SelectionOptions().Mode);
            CaptureCommandReject(() => CaptureCommandLine.Resolve(command.Request with { Schedule = new CaptureSchedule.Delay(1) }, settings));
            var monitor = new Rectangle(0, 0, 400, 300);
            var session = new SessionController(command.SelectionOptions() with { Intent = CaptureIntent.Recording },
                new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]), []);
            try
            {
                Equal(CaptureMode.Rectangle, session.Mode);
                Check(!session.SelectionMenu.AllCommands.Any(item => item.Name is "ModeScroll" || item.Name == preset.Id),
                    "録画のメニューで手動合成を選べる");
            }
            finally { session.Cancel(); }
        });
        Run("操作窓は負座標の画面も含めて選択領域と重ならない位置を優先する", () =>
        {
            Rectangle viewport = new(-900, 0, 900, 700), available = new(0, -100, 1000, 700);
            Size panel = new(350, 90);
            Point location = ScrollCaptureForm.FindPanelLocation([available, viewport], viewport, panel);
            Check(available.Contains(new Rectangle(location, panel)), "他のモニターの空き領域を利用しなかった");
            Rectangle region = new(-790, 100, 460, 300);
            location = ScrollCaptureForm.FindPanelLocation([available, viewport], region, panel);
            Check(viewport.Contains(new Rectangle(location, panel)) && !region.IntersectsWith(new(location, panel)), "同じモニターの空き領域を利用しなかった");
        });
        Run("スクロール合成の出力はコピー・保存で一致し、白い余白は継ぎ目に入らない", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var stitch = new ScrollFrameStitcher(new(20, 25), 5);
                using var first = ScrollRows(20, 25, 0);
                using var second = ScrollRows(20, 25, 12);
                stitch.Add(first);
                stitch.Add(second);
                using var image = stitch.Build();
                Rectangle viewport = new(20, 30, 20, 25);
                var display = new DisplayLayout("fixture", "表示", new(0, 0, 400, 300), 96, 96);
                var source = new CaptureReplaySource.Scroll(viewport);
                var settings = OutputSettings(directory) with
                {
                    SaveDirectory = directory, FileNamePattern = "scroll.png", ScreenshotPaddingPixels = 5,
                    DefaultDestination = OutputDestination.File | OutputDestination.Clipboard
                };
                LastCaptureRecipe? recipe = null;
                using var output = new OutputRouter(Path.Combine(directory, "work"), copyImage: copy =>
                {
                    Equal(new Size(30, 47), copy.Size);
                    Equal(Color.White.ToArgb(), copy.GetPixel(0, 0).ToArgb());
                    for (int y = 0; y < image.Height; y++)
                        Equal(image.GetPixel(0, y), copy.GetPixel(5, y + 5));
                    return Task.CompletedTask;
                }, captureSucceeded: (_, value) => recipe = value, readCaptureLayout: () => [display]);
                await output.ScreenshotAsync(image, settings.DefaultDestination, settings, captureBounds: viewport, replaySource: source);
                using var saved = new Bitmap(Path.Combine(directory, "scroll.png"));
                Equal(new Size(30, 47), saved.Size);
                for (int y = 0; y < image.Height; y++)
                    Equal(image.GetPixel(0, y).ToArgb(), saved.GetPixel(5, y + 5).ToArgb());
                Check(recipe is { RequiresScroll: true, RectangularRegion: null }, "スクロール範囲を固定範囲として記録した");
                Equal(viewport, recipe!.Resolve([display]));
                Equal(5, recipe.Settings.ScreenshotPaddingPixels);
            })));
    }

    private static int ScrollDesktopSmoke()
    {
        Run("テスト窓のホイールと実デスクトップ画素からスクロール撮影を確定・取り消しできる", () =>
        {
            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? throw new InvalidOperationException("表示画面がありません。");
            using var fixture = new Form
            {
                Text = "SSCapture スクロール検証", StartPosition = FormStartPosition.Manual,
                Location = new(work.Left + 48, work.Top + 48), ClientSize = new(200, 140), TopMost = true,
                ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None
            };
            const int step = 23;
            int offset = 0;
            fixture.Paint += (_, args) =>
            {
                using Bitmap image = ScrollRows(fixture.ClientSize.Width, fixture.ClientSize.Height, offset);
                args.Graphics.DrawImageUnscaled(image, 0, 0);
            };
            fixture.MouseWheel += (_, args) => { offset += args.Delta < 0 ? step : -step; fixture.Refresh(); };
            CaptureNative.DisableTransitions(fixture);
            fixture.Show();
            fixture.Refresh();
            CaptureNative.SynchronizeDesktop();
            Rectangle viewport = fixture.RectangleToScreen(Rectangle.Inflate(fixture.ClientRectangle, -8, -8));
            Task<(Bitmap Image, CaptureMetadata? Metadata)?>? capture = null;
            try
            {
                foreach (bool accept in new[] { true, false })
                {
                    using (var firstSample = InstantCapture.Take(viewport))
                    using (var expectedSample = ScrollRows(viewport.Width, viewport.Height, offset + 8))
                        for (int y = 0; y < viewport.Height; y++)
                            Equal(expectedSample.GetPixel(15, y), firstSample.GetPixel(15, y));
                    capture = ScrollCapture.CollectAsync(viewport, new AppSettings { DefaultDestination = OutputDestination.Clipboard }, fixture.Handle);
                    ScrollCaptureForm? panel = null;
                    WaitFor(() => (panel = Application.OpenForms.OfType<ScrollCaptureForm>().SingleOrDefault())?.Visible == true, () => "操作窓の表示");
                    Point center = new(viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2);
                    int coordinates = (center.X & 0xffff) | center.Y << 16;
                    if (accept)
                    {
                        SendScrollFixtureMessage(fixture.Handle, 0x020A, unchecked(-120 << 16), coordinates);
                        Equal(step, offset);
                        CaptureNative.SynchronizeDesktop();
                        var status = panel!.Controls.Find("ScrollCaptureStatus", true).Single();
                        Check(!panel.Bounds.IntersectsWith(viewport), "操作窓が検証範囲を隠した");
                        using (var actualSample = InstantCapture.Take(viewport))
                        using (var expectedSample = ScrollRows(viewport.Width, viewport.Height, offset + 8))
                            Equal(expectedSample.GetPixel(15, 15), actualSample.GetPixel(15, 15));
                        WaitFor(() => status.Text == $"{viewport.Width}×{viewport.Height + step} px", () => status.Text);
                        ((Button)panel!.Controls.Find("ConfirmScrollCapture", true).Single()).PerformClick();
                    }
                    else
                        ((Button)panel!.Controls.Find("CancelScrollCapture", true).Single()).PerformClick();
                    WaitFor(() => capture.IsCompleted, () => "撮影の完了");
                    var result = capture.GetAwaiter().GetResult();
                    capture = null;
                    if (result is { } completed)
                    {
                        using var actual = completed.Image;
                        Check(accept, "取り消した画像を受け取った");
                        Equal(new Size(viewport.Width, viewport.Height + step), actual.Size);
                        using var expected = ScrollRows(actual.Width, actual.Height, 8);
                        for (int y = 0; y < actual.Height; y++)
                            Equal(expected.GetPixel(15, y), actual.GetPixel(15, y));
                    }
                    else Check(!accept, "確定した画像を受け取れなかった");
                    Check(panel!.IsDisposed, "撮影後に操作窓が残った");
                }
            }
            finally
            {
                foreach (ScrollCaptureForm panel in Application.OpenForms.OfType<ScrollCaptureForm>().ToArray()) panel.Dispose();
                if (capture is { IsCompletedSuccessfully: true } && capture.Result is { } remainder) remainder.Image.Dispose();
            }
        });
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;

        static void WaitFor(Func<bool> condition, Func<string> describe)
        {
            var time = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && time.ElapsedMilliseconds < 6000)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
            Check(condition(), "スクロール画面の更新が時間内に確認できなかった: " + describe());
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendScrollFixtureMessage(nint window, uint message, nint parameter, nint coordinates);

    private static Bitmap ScrollRows(int width, int height, int first, int? period = null)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using Graphics g = Graphics.FromImage(frame);
        for (int y = 0; y < height; y++)
        {
            int row = (first + y) % (period ?? int.MaxValue);
            using var color = new SolidBrush(Color.FromArgb(32, (row >> 8) & 255, row & 255));
            g.FillRectangle(color, 0, y, width, 1);
        }
        return frame;
    }
}
