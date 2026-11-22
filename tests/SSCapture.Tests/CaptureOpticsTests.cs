using System.Reflection;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureOpticsTests()
    {
        Run("四方向の矩形で拡大鏡の赤線と切り出し端が同じ物理画素に合う", () =>
        {
            Rectangle source = new(-10, -9, 20, 18), lens = new(0, 0, 90, 90);
            using var frame = new Bitmap(source.Width, source.Height);
            using (Graphics graphics = Graphics.FromImage(frame)) graphics.Clear(Color.Lime);
            Rectangle selection = new(-3, -2, 6, 4);
            using (Graphics graphics = Graphics.FromImage(frame))
                graphics.FillRectangle(Brushes.Cyan, selection.X - source.X, selection.Y - source.Y, selection.Width, selection.Height);
            using var cropped = CaptureGeometry.Crop(frame, source, selection)!;
            foreach (int x in new[] { selection.Left, selection.Right })
                foreach (int y in new[] { selection.Top, selection.Bottom })
                {
                    Point sample = new(x, y);
                    using var image = new Bitmap(90, 90);
                    using (Graphics graphics = Graphics.FromImage(image))
                        CaptureMagnifier.Draw(graphics, lens, frame, source, sample, selection);
                    int inX = x == selection.Left ? 45 : 35, outX = x == selection.Left ? 35 : 45;
                    int inY = y == selection.Top ? 45 : 35, outY = y == selection.Top ? 35 : 45;
                    Equal(cropped.GetPixel(x == selection.Left ? 0 : cropped.Width - 1, y == selection.Top ? 0 : cropped.Height - 1).ToArgb(),
                        image.GetPixel(inX, inY).ToArgb());
                    Equal(Color.Red.ToArgb(), image.GetPixel(40, inY).ToArgb());
                    Equal(Color.Red.ToArgb(), image.GetPixel(inX, 40).ToArgb());
                    Equal(Color.White.ToArgb(), image.GetPixel(41, 45).ToArgb());
                    Color outside = image.GetPixel(outX, outY);
                    Check(outside.G is > 0 and < 255 && outside.B == 0, "範囲外の画素の減光が一致しない");
                }
        });
        Run("拡大鏡は離れたウィンドウ・モニター境界とグリッドの補正済み端点を使う", () =>
        {
            Rectangle monitor = new(-400, -400, 400, 400), window = new(-288, -258, 100, 100);
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [monitor], [window]);
            gesture.Begin(new(-298, -240), new(2, 2), snap: true);
            Equal(new Point(-288, -240), gesture.MagnifierPoint);
            gesture.Move(new(-199, -167), snap: true);
            Equal(new Point(-188, -158), gesture.MagnifierPoint);
            Equal(new Rectangle(-288, -240, 100, 82), gesture.Selection);
            gesture.Move(new(-4, -1), snap: true);
            Equal(Point.Empty, gesture.MagnifierPoint);
            gesture.Move(new(-253, -191), action: CaptureKeyAction.Grid);
            Equal(new Point(-248, -200), gesture.MagnifierPoint);
            Equal(new Point(-253, -191), gesture.RawCurrent);
        });
        Run("拡大鏡は画面四隅・四辺でガイドを避け、空きが戻ると再表示する", () =>
        {
            Rectangle viewport = new(-500, -300, 500, 300);
            Rectangle separateGuide = new(0, -300, 250, 200);
            foreach (int x in new[] { viewport.Left, -250, viewport.Right - 1 })
                foreach (int y in new[] { viewport.Top, -150, viewport.Bottom - 1 })
                {
                    Point pointer = new(x, y);
                    Rectangle hint = new(Math.Clamp(x + 18, -500, -170), Math.Clamp(y + 24, -300, -85), 170, 85);
                    Rectangle dimensions = new(Math.Clamp(x + 18, -500, -70), Math.Clamp(y - 30, -300, -28), 70, 28);
                    Rectangle lens = CaptureMagnifier.Place(viewport, pointer, [hint, dimensions, separateGuide], -1).Bounds;
                    Check(!lens.IsEmpty && viewport.Contains(lens), "四隅・四辺で有効な空き領域を見つけられない");
                    Check(!lens.IntersectsWith(hint) && !lens.IntersectsWith(dimensions) && !lens.Contains(pointer), "拡大鏡が文字・ポインタに重なった");
                }
            Point center = new(-250, -150);
            var placed = CaptureMagnifier.Place(viewport, center, [], previousQuadrant: 3);
            Equal(3, placed.Quadrant);
            Equal(3, CaptureMagnifier.Place(viewport, center + new Size(1, -2), [], placed.Quadrant).Quadrant);
            Equal(Rectangle.Empty, CaptureMagnifier.Place(viewport, center, [viewport], placed.Quadrant).Bounds);
            Check(!CaptureMagnifier.Place(viewport, center, [], -1).Bounds.IsEmpty, "ガイドを隠しても拡大鏡が戻らない");
        });
        Run("四隅が塞がった場合は同じモニターの最も近い空き領域を使う", () =>
        {
            Rectangle viewport = new(0, 0, 320, 320);
            Point pointer = new(160, 160);
            Rectangle[] labels = [new(46, 46, 90, 90), new(184, 46, 90, 90), new(46, 184, 90, 90), new(184, 184, 90, 90)];
            // Move the lower guide across the bottom right. The nearest opening is
            // now centered above the pointer between the two upper guides.
            labels[2] = new(0, 184, 105, 90);
            labels[3] = new(215, 184, 105, 90);
            var placement = CaptureMagnifier.Place(viewport, pointer, labels, -1);
            Check(!placement.Bounds.IsEmpty, "空き領域を見つけられない");
            Equal(-1, placement.Quadrant);
            var occupied = labels.Append(new Rectangle(pointer, new Size(1, 1)))
                .Select(r => Rectangle.Inflate(r, 8, 8)).ToArray();
            int Distance(Rectangle lens) => (lens.X + 45 - pointer.X) * (lens.X + 45 - pointer.X)
                + (lens.Y + 45 - pointer.Y) * (lens.Y + 45 - pointer.Y);
            int minimum = (from y in Enumerable.Range(0, 231)
                           from x in Enumerable.Range(0, 231)
                           let lens = new Rectangle(x, y, 90, 90)
                           where !occupied.Any(lens.IntersectsWith)
                           select Distance(lens)).Min();
            Equal(minimum, Distance(placement.Bounds));
        });
        Run("分割ホイール入力、元枠への復帰、対象移動と取消を選択枠と表示に反映する", () =>
        {
            Rectangle desktop = new(0, 0, 120, 100);
            var first = new CaptureWindow(new(2, 10, 20, 10), "左", 1, 0, 0);
            var other = new CaptureWindow(new(40, 30, 30, 20), "右", 2, 0, 0);
            CaptureWindow[] targets = [first, other];
            var session = new SessionController(new(CaptureMode.Window, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]),
                targets.Select(window => window.Bounds).ToArray(), findWindow: p => targets.FirstOrDefault(window => window.Bounds.Contains(p)));
            try
            {
                Point point = first.Bounds.Location + new Size(3, 4);
                session.MouseWheel(point, 60);
                Equal(first.Bounds, session.DisplaySelection);
                session.MouseWheel(point, 300);
                Equal(Rectangle.Inflate(first.Bounds, 2, 2), session.DisplaySelection);
                Check(session.PointerHint!.Contains("座標（0, 8　24 × 14）　余白+2px"), "適用済みの余白と表示が合わない");
                session.MouseWheel(point, 120);
                Equal(Rectangle.Inflate(first.Bounds, 2, 2), session.MagnifierSelection);
                session.MouseWheel(point, -120);
                Equal(Rectangle.Inflate(first.Bounds, 1, 1), session.DisplaySelection);
                session.MoveTo(point + new Size(1, 0));
                Check(session.PointerHint!.EndsWith("余白+1px"), "同一対象で余白を失った");
                session.MouseWheel(point, -120);
                Check(session.PointerHint!.EndsWith("余白0px"), "元の枠へ戻っていない");
                session.MouseWheel(point, -1200);
                Equal(Rectangle.Inflate(first.Bounds, -4, -4), session.DisplaySelection);
                session.MouseWheel(point, 60);
                session.MoveTo(other.Bounds.Location);
                session.MouseWheel(other.Bounds.Location, 60);
                Check(session.PointerHint!.EndsWith("余白0px"), "対象切替で以前の量か端数が残った");
                session.MouseWheel(other.Bounds.Location, 60);
                Equal(Rectangle.Inflate(other.Bounds, 1, 1), session.DisplaySelection);
                session.AbortSelection();
                Equal(other.Bounds, session.DisplaySelection);
            }
            finally { session.Cancel(); }
        });
        Run("ホイールで縮めた単一ウィンドウをクリックすると枠どおりに撮影し再撮影条件へ渡す", () =>
        {
            Rectangle desktop = new(-60, -40, 120, 100);
            var target = new CaptureWindow(new(-20, -10, 30, 20), "対象", 1, 0, 0);
            var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]);
            using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.RoyalBlue);
            snapshot.Image.SetPixel(target.Bounds.Left - desktop.Left + 1, target.Bounds.Top - desktop.Top + 1, Color.Lime);
            Point point = target.Bounds.Location + new Size(6, 6);
            var session = new SessionController(new(CaptureMode.Window, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                snapshot, [target.Bounds], findWindow: _ => target);
            try
            {
                session.MouseWheel(point, -120);
                var bounds = Rectangle.Inflate(target.Bounds, -1, -1);
                Equal(bounds, session.DisplaySelection);
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(SessionController).GetField("pressedButton", fields)!.SetValue(session, MouseButtons.Left);
                var gesture = (SelectionGesture)typeof(SessionController).GetField("gesture", fields)!.GetValue(session)!;
                gesture.Begin(point, new(4, 4));
                session.MouseUp(MouseButtons.Left, point, Keys.None);
                Check(session.Completion.IsCompletedSuccessfully, "確定していない");
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(bounds, outcome.Selection!.Bounds);
                Equal(bounds.Size, outcome.Image!.Size);
                Equal(Color.Lime.ToArgb(), outcome.Image.GetPixel(0, 0).ToArgb());
                var source = (CaptureReplaySource.Windows)outcome.Selection.ReplaySource!;
                Equal<int?>(-1, source.TargetMargin);
                Check(source.IsSingleWindowSelection, "出力プリセットの切り抜き対象から外れた");
            }
            finally { session.Cancel(); }
        });
        Run("WGC部品の確定枠は開始モニターをまたぐ調整量も保持する", () =>
        {
            Rectangle monitor = new(0, 0, 120, 100);
            Rectangle extended = new(106, 29, 17, 15);
            var target = new CaptureWindow(new(80, 10, 60, 60), "対象", 1, 2, 3);
            var session = new SessionController(new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot,
                OutputDestination.Clipboard) { SelectionOnly = true },
                new DesktopSnapshot(new Bitmap(monitor.Width, monitor.Height), monitor, [monitor]), [target.Bounds]);
            try
            {
                var source = new CaptureReplaySource.Control(target, new("part", 50000), true) { TargetMargin = 4 };
                session.CompleteSelection(new(extended, null, OutputDestination.Clipboard, ControlSource: source));
                Check(session.Completion.IsCompletedSuccessfully, "WGCの確定が開始モニターの端で拒否された");
                using var result = session.Completion.GetAwaiter().GetResult();
                Equal(extended, result.Selection!.Bounds);
                Equal<CaptureReplaySource>(source, result.Selection.ReplaySource!);
            }
            finally { session.Cancel(); }
        });
        Run("デスクトップ再撮影は移動先のウィンドウ・部品の枠とモニターの空白を反映する", () =>
        {
            Rectangle[] screens = [new(-150, -80, 100, 160), new(-30, -80, 160, 160)];
            Rectangle original = new(0, 0, 22, 20), moved = new(-140, -20, 82, 30);
            var window = new CaptureWindow(original, "対象", 1, 2, 3);
            var identity = new ControlIdentity("fixture", 50000);
            foreach (CaptureReplaySource source in new CaptureReplaySource[]
            {
                new CaptureReplaySource.Windows([window], false, false, true) { TargetMargin = 12 },
                new CaptureReplaySource.Control(window, identity, false) { TargetMargin = 12 }
            })
            {
                var recipe = LastCaptureRecipe.Create(1, original, null, new(), [], source, _ => 9);
                Equal<int?>(12, recipe.TargetMargin);
                var resolved = recipe.ResolveWindows([window with { Bounds = moved }], _ => 9).Single();
                Equal(Rectangle.Inflate(moved, 8, 8), InstantCapture.ResolveDesktopMargin(resolved.Bounds, recipe.TargetMargin!.Value, screens, 2));
            }
            Rectangle spanning = new(-62, -16, 44, 16);
            Rectangle[] adjacent = [screens[0], new(-50, -80, 180, 160)];
            Equal(Rectangle.Inflate(spanning, 12, 12), InstantCapture.ResolveDesktopMargin(spanning, 12, adjacent, 1));
            Equal(spanning, InstantCapture.ResolveDesktopMargin(spanning, 12, screens, 1));
            Equal(new Rectangle(1, 1, 3, 1), InstantCapture.ResolveDesktopMargin(new(0, 0, 5, 3), -20, screens, 1));
        });
        Run("ホイール調整後に出力プリセットの内側切り抜きと白余白を一度ずつ適用する", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                Rectangle original = new(-50, -30, 12, 10), bounds = Rectangle.Inflate(original, -1, -1);
                var window = new CaptureWindow(original, "対象", 1, 2, 3);
                var source = new CaptureReplaySource.Windows([window], false, false, true) { TargetMargin = -1 };
                using var image = new Bitmap(bounds.Width, bounds.Height);
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.DodgerBlue);
                image.SetPixel(1, 1, Color.Lime);
                LastCaptureRecipe? recorded = null;
                int copies = 0;
                using var router = new OutputRouter(directory, output =>
                {
                    copies++;
                    Equal(new Size(12, 10), output.Size);
                    Equal(Color.White.ToArgb(), output.GetPixel(1, 1).ToArgb());
                    Equal(Color.Lime.ToArgb(), output.GetPixel(2, 2).ToArgb());
                    Equal(Color.DodgerBlue.ToArgb(), output.GetPixel(9, 7).ToArgb());
                    return Task.CompletedTask;
                }, captureSucceeded: (_, recipe) => recorded = recipe);
                await router.ScreenshotAsync(image, OutputDestination.Clipboard,
                    new AppSettings { WindowCrop = new(1, 1, 1, 1), ScreenshotPaddingPixels = 2 },
                    captureBounds: bounds, replaySource: source);
                Equal(1, copies);
                Equal(bounds, recorded!.Bounds);
                Equal<int?>(-1, recorded.TargetMargin);
            })));
        Run("WGC再撮影は部品の拡張と単一ウィンドウの縮小を新しい画像へ適用する", () =>
        {
            Rectangle frameBounds = new(-170, -40, 16, 12);
            Rectangle controlBounds = new(-168, -36, 6, 4);
            var window = new CaptureWindow(frameBounds, "対象", 1, 2, 3);
            DateTimeOffset at = DateTimeOffset.Now;
            var context = new CaptureContext(at, new("ok"), new("ok"), new("ok"), new("ok"));
            var metadata = CaptureMetadata.Create(frameBounds, 3, at, new(at, new(-170, -40), true, true), context);
            foreach (var (source, original, margin, minimumSize, expected) in new (CaptureReplaySource, Rectangle, int, int, Rectangle)[]
            {
                (new CaptureReplaySource.Control(window, new("part", 50000), true) { TargetMargin = 3 }, controlBounds, 3, 1, new(-170, -38, 10, 8)),
                (new CaptureReplaySource.Windows([window], true, false, true) { TargetMargin = -2 }, frameBounds, -2, 2, new(-168, -38, 12, 8))
            })
            {
                var recipe = LastCaptureRecipe.Create(1, original, null, new(), [], source, _ => 9);
                Equal<int?>(margin, recipe.TargetMargin);
                using var frame = new Bitmap(frameBounds.Width, frameBounds.Height);
                frame.SetPixel(expected.Left - frameBounds.Left, expected.Top - frameBounds.Top, Color.Lime);
                frame.SetPixel(expected.Right - 1 - frameBounds.Left, expected.Bottom - 1 - frameBounds.Top, Color.Blue);
                var replay = InstantCapture.CropIsolatedReplay((frame, frameBounds, metadata), original,
                    recipe.TargetMargin!.Value, minimumSize, 3, window.Title, source);
                using var cropped = replay.Image;
                Equal(expected, replay.Bounds);
                Equal(PixelBounds.From(expected), replay.Metadata!.CaptureBounds);
                Equal(new PixelSize(expected.Width + 6, expected.Height + 6), replay.Metadata.OutputSize);
                Equal(expected.Size, cropped.Size);
                Equal(Color.Lime.ToArgb(), cropped.GetPixel(0, 0).ToArgb());
                Equal(Color.Blue.ToArgb(), cropped.GetPixel(cropped.Width - 1, cropped.Height - 1).ToArgb());
            }
        });
    }
}
