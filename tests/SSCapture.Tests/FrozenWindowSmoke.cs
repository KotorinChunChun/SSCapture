using System.Diagnostics;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunFrozenWindowSmoke()
    {
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        using var host = new Form
        {
            Text = WindowTitle.Create("画面固定の検証"),
            StartPosition = FormStartPosition.Manual,
            Bounds = new(work.X + 64, work.Y + 64, 460, 290),
            BackColor = Color.RoyalBlue
        };
        var button = new Button
        {
            Name = "FrozenButton", Text = "検証", Bounds = new(70, 100, 180, 68),
            BackColor = Color.DarkOrange, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
        };
        host.Controls.Add(button);
        Exception? error = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(async () =>
        {
            SessionController? session = null;
            List<Form> fixtures = [];
            try
            {
                NativeSmokeInput.RequireNoHeldInput();
                for (int index = 0; index < 24; index++)
                {
                    var fixture = new FrozenBackgroundFixture
                    {
                        Text = $"SSCapture 検証窓 {index + 1}", ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Bounds = new Rectangle(work.Right - 344 + index % 3 * 96, work.Y + 20 + index / 3 * 58, 90, 50),
                        FormBorderStyle = FormBorderStyle.None
                    };
                    fixtures.Add(fixture);
                    fixture.Show();
                }
                NativeSmokeInput.FocusFixture(host.Handle);
                NativeSmokeInput.RequireForeground(host.Handle);
                CaptureNative.SynchronizeDesktop();
                Rectangle control = button.RectangleToScreen(button.ClientRectangle);
                Rectangle desktop = CaptureNative.MonitorBounds().Aggregate(Rectangle.Union);
                var windows = CaptureNative.CaptureWindows(desktop);
                var target = windows.Single(window => window.Handle == host.Handle);
                var corner = control.Location + new Size(12, 12);
                Point outside = work.Location + new Size(12, 12);
                Point point = control.Location + new Size(50, 22);
                await Capture(outside, point, control, corner, 0xA2, includeCursor: false);
                // Changing a different window between sessions must leave the target readily usable.
                fixtures[0].Left += 7;
                await Capture(point, point + new Size(35, 5), target.Bounds, corner, 0x5B, includeCursor: true);
                Console.WriteLine($"FROZEN WINDOW PASS windows={windows.Count} fixtures={fixtures.Count} Ctrl/Win preview=image");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                session?.Cancel();
                foreach (var fixture in fixtures) fixture.Dispose();
                host.Close();
            }

            async Task Capture(Point start, Point select, Rectangle expected, Point colorPoint, byte key, bool includeCursor)
            {
                using var input = new NativeSmokeInput();
                NativeSmokeInput.FocusFixture(host.Handle);
                NativeSmokeInput.RequireForeground(host.Handle);
                input.Move(start);
                var stopwatch = Stopwatch.StartNew();
                var pending = CaptureSession.SelectAsync(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    FreezeFrame: true, LimitToStartingMonitor: false, ShowSelectionGuide: false, IncludeCursor: includeCursor));
                CaptureOverlay? overlay = null;
                await WaitUntil(() => (overlay = Application.OpenForms.OfType<CaptureOverlay>()
                    .FirstOrDefault(value => value.Visible && value.MonitorBounds.Contains(select))) is not null, "固定の選択画面を表示できません。");
                session = OverlaySession(overlay!);
                long startMs = stopwatch.ElapsedMilliseconds;
                input.Move(select);
                NativeSmokeInput.RequirePointTarget(overlay!.Handle, select);
                stopwatch.Restart();
                input.KeyDown(key);
                await WaitUntil(() => key == 0xA2 ? session.ControlBounds == expected : session.PreviewFrame?.Bounds == expected,
                    $"固定の実キーで対象枠を取得できません（{key:X}）。");
                var preview = session.PreviewFrame!;
                using var before = key == 0xA2
                    ? session.Snapshot.Image.Clone(new Rectangle(expected.Location - new Size(session.Snapshot.Bounds.Location), expected.Size),
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                    : (Bitmap)preview.Image.Clone();
                Equal(Pixel(before, expected, colorPoint), Pixel(preview.Image, preview.Bounds, colorPoint));
                long inputMs = stopwatch.ElapsedMilliseconds;
                NativeSmokeInput.RequirePointTarget(overlay.Handle, select);
                input.LeftDown();
                await Task.Delay(65);
                input.LeftUp();
                using var result = await pending.WaitAsync(TimeSpan.FromSeconds(4));
                input.KeyUp(key);
                Equal(expected, result.Selection!.Bounds);
                Equal(expected.Size, result.Image!.Size);
                for (int y = 0; y < before.Height; y++)
                for (int x = 0; x < before.Width; x++)
                    Equal(before.GetPixel(x, y).ToArgb(), result.Image.GetPixel(x, y).ToArgb());
                Console.WriteLine($"PASS key={key:X} includeCursor={includeCursor} start={startMs}ms target={inputMs}ms area={expected}");
                session = null;
            }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }

    private sealed class FrozenBackgroundFixture : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    // Uses real desktop pixels and WGC without moving the pointer, injecting keys or touching the clipboard.
    private static int RunCoveredWindowWgcTest()
    {
        Point origin = Screen.PrimaryScreen!.WorkingArea.Location + new Size(70, 70);
        Rectangle area = new(origin, new Size(240, 150));
        Rectangle coverArea = new(origin + new Size(110, 35), new Size(102, 90));
        Point pick = origin + new Size(12, 14), covered = coverArea.Location + new Size(15, 16);
        using Form target = TestWindow(area, Color.RoyalBlue);
        using Form cover = TestWindow(coverArea, Color.DarkOrange);
        Exception? error = null;
        target.Shown += (_, _) => target.BeginInvoke((Action)(async () =>
        {
            SessionController? session = null;
            try
            {
                cover.TopMost = true;
                cover.Show();
                await Task.Delay(160);
                CaptureNative.SynchronizeDesktop();
                DesktopSnapshot snapshot = DesktopSnapshot.Take(captureTargets: true);
                var window = (snapshot.FrozenTargets?.SelectableWindows ?? []).SingleOrDefault(window => window.Handle == target.Handle);
                if (window is null || !window.Bounds.Contains(pick))
                {
                    snapshot.Dispose();
                    throw new InvalidOperationException("テスト対象ウィンドウの位置を取得できません。");
                }
                session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    FreezeFrame: true, LimitToStartingMonitor: false, ShowSelectionGuide: false), snapshot, [window.Bounds],
                    initialPointer: pick, findWindow: point => window.Bounds.Contains(point) ? window : null);
                Equal(Color.DarkOrange.ToArgb(), Pixel(snapshot.Image, snapshot.Bounds, covered));
                session.ApplyPointerInput(pick, CaptureModifiers.Win);
                await WaitUntil(() => session.PreviewFrame is not null, "WGCのプレビューが取得できません。");
                var preview = session.PreviewFrame!;
                Equal(Color.RoyalBlue.ToArgb(), Pixel(preview.Image, preview.Bounds, covered));
                session.CompleteSelection(new(window.Bounds, null, OutputDestination.Clipboard, WindowHitPoint: pick));
                using var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                Equal(preview.Bounds, result.Selection!.Bounds);
                Check(ReferenceEquals(preview.Image, result.Image), "WGCのプレビューが確定に使われませんでした。");
                Console.WriteLine("WGC PASS: 固定画面は手前の色、プレビューと撮影結果は背面の色");
            }
            catch (Exception ex) { error = ex; }
            finally { session?.Cancel(); cover.Close(); target.Close(); }
        }));
        Application.Run(target);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;

        static Form TestWindow(Rectangle bounds, Color color) => new()
        {
            StartPosition = FormStartPosition.Manual, Bounds = bounds, FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false, BackColor = color, Text = WindowTitle.Create("ウィンドウ撮影の検証")
        };
    }
}
