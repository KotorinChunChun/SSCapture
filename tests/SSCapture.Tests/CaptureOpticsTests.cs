using System.Reflection;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureOpticsTests()
    {
        CrosshairGuideTests();
        SelectionFrameTests();
        Run("通常の矩形モードでは左単押し中のホイールを確定画像の四辺に反映する", () =>
        {
            Rectangle desktop = new(-100, -80, 1000, 760), original = new(-20, 0, 800, 600);
            Rectangle expected = Rectangle.Inflate(original, 2, 2);
            var target = new CaptureWindow(original, "対象", 1, 0, 0);
            var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]);
            using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.White);
            (Point Pixel, Color Color)[] edges =
            [
                (new(expected.Left, original.Top + 10), Color.Lime),
                (new(expected.Right - 1, original.Top + 10), Color.Blue),
                (new(original.Left + 10, expected.Top), Color.Red),
                (new(original.Left + 10, expected.Bottom - 1), Color.Magenta)
            ];
            foreach (var (pixel, color) in edges)
                snapshot.Image.SetPixel(pixel.X - desktop.Left, pixel.Y - desktop.Top, color);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                OutputSettings: new() { ScreenshotPaddingPixels = 7 }), snapshot, [original], findWindow: p => original.Contains(p) ? target : null);
            try
            {
                Point point = original.Location + new Size(10, 10);
                session.MouseWheel(point, 240);
                Equal(Rectangle.Empty, session.DisplaySelection);
                BeginOpticsPress(session, point);
                session.MouseWheel(point, 240);
                Equal(expected, session.DisplaySelection);
                Equal(original, session.SelectionBasis);
                Equal(expected, session.MagnifierSelection);
                session.MouseWheel(point, -240);
                Equal(original, session.DisplaySelection);
                Equal(original, session.SelectionBasis);
                session.MouseWheel(point, 240);
                session.MouseUp(MouseButtons.Left, point, Keys.None);
                Check(session.Completion.IsCompletedSuccessfully, "単押しを確定できない");
                using var outcome = session.Completion.Result;
                Equal(expected, outcome.Selection!.Bounds);
                Equal(expected.Size, outcome.Image!.Size);
                Equal<int?>(2, ((CaptureReplaySource.Windows)outcome.Selection.ReplaySource!).TargetMargin);
                foreach (var (pixel, color) in edges)
                    Equal(color.ToArgb(), outcome.Image.GetPixel(pixel.X - expected.Left, pixel.Y - expected.Top).ToArgb());
            }
            finally { session.Cancel(); }
        });
        Run("拡大鏡の1点目は偶数ドットを青い十字で四等分し各画素の色を保つ", () =>
        {
            const int dots = 6, zoom = 7, size = dots * zoom, center = size / 2;
            Rectangle source = new(-20, -30, 40, 40), lens = new(0, 0, size, size);
            Point boundary = new(-3, -4);
            using var frame = new Bitmap(source.Width, source.Height);
            Color[] colors = [Color.Lime, Color.Cyan, Color.Yellow, Color.Magenta];
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                    frame.SetPixel(x, y, colors[(x < boundary.X - source.X ? 0 : 1) + (y < boundary.Y - source.Y ? 0 : 2)]);
            using var image = new Bitmap(size, size);
            using (Graphics graphics = Graphics.FromImage(image))
                CaptureMagnifier.Draw(graphics, lens, frame, source, boundary,
                    dotCount: dots, zoom: zoom);
            for (int y = 1; y < size - 1; y++)
                for (int x = 1; x < size - 1; x++)
                {
                    Color expected = x == center || y == center ? Color.DodgerBlue
                        : colors[(x < center ? 0 : 1) + (y < center ? 0 : 2)];
                    Equal(expected.ToArgb(), image.GetPixel(x, y).ToArgb());
                }
        });
        Run("拡大鏡は合成領域の共有境界を省き、範囲外の隙間を暗くする", () =>
        {
            Rectangle source = new(-4, -4, 8, 8), lens = new(0, 0, 32, 32);
            using var frame = new Bitmap(8, 8);
            using var image = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(frame)) graphics.Clear(Color.Lime);
            using var magnifier = Graphics.FromImage(image);
            CaptureMagnifier.Draw(magnifier, lens, frame, source, Point.Empty, dotCount: 8, zoom: 4,
                regions: [new(-3, -3, 3, 6), new(0, -3, 3, 6)]);
            Equal(Color.DodgerBlue.ToArgb(), image.GetPixel(16, 10).ToArgb());
            Equal(Color.Lime.ToArgb(), image.GetPixel(18, 10).ToArgb());
            CaptureMagnifier.Draw(magnifier, lens, frame, source, Point.Empty, dotCount: 8, zoom: 4,
                regions: [new(-3, -3, 3, 6), new(1, -3, 2, 6)]);
            Equal(Color.Red.ToArgb(), image.GetPixel(16, 10).ToArgb());
            Equal(Color.Red.ToArgb(), image.GetPixel(20, 10).ToArgb());
            Check(image.GetPixel(18, 10).G is > 0 and < 255, "領域間の隙間が暗くならない");
        });
        Run("矩形ホイールで800×600を広げても拡大鏡は実ポインタで撮影枠は804×604", () =>
        {
            Rectangle desktop = new(-200, -100, 1280, 960);
            using var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                snapshot, []);
            try
            {
                var start = new Point(-100, -50);
                var end = new Point(700, 550);
                Rectangle basis = new(start, new Size(800, 600));
                BeginOpticsPress(session, start);
                session.MoveTo(end);
                session.MouseWheel(end, 240);
                Equal(end, session.MagnifierSample);
                Equal(Rectangle.Inflate(basis, 2, 2), session.MagnifierSelection);
                Equal("各辺 +2 px", session.SelectionMarginHint);
                Check(session.PointerHint!.EndsWith("各辺 +2 px"), "ポインタ横のガイドに各辺の量が出ない");
                session.MouseWheel(end, -240);
                Equal(basis, session.MagnifierSelection);
                Equal("0 px", session.SelectionMarginHint);
                session.MouseWheel(end, 240);
                session.MouseUp(MouseButtons.Left, end);
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(Rectangle.Inflate(basis, 2, 2), outcome.Selection!.Bounds);
                Equal(new Size(804, 604), outcome.Image!.Size);
            }
            finally { session.Cancel(); }
        });
        Run("四方向の矩形で拡大鏡の赤線と明るい象限が実際の切り抜き端に合う", () =>
        {
            const int dots = 8, zoom = 8, size = dots * zoom, center = size / 2;
            Rectangle source = new(-10, -9, 20, 18), lens = new(0, 0, size, size);
            using var frame = new Bitmap(source.Width, source.Height);
            using (Graphics graphics = Graphics.FromImage(frame)) graphics.Clear(Color.Lime);
            Rectangle selection = new(-3, -2, 6, 4);
            foreach (int x in new[] { selection.Left, selection.Right })
                foreach (int y in new[] { selection.Top, selection.Bottom })
                {
                    bool endsRight = x == selection.Right, endsBottom = y == selection.Bottom;
                    Point start = new(endsRight ? selection.Left : selection.Right, endsBottom ? selection.Top : selection.Bottom);
                    Point end = new(x, y);
                    var gesture = new SelectionGesture(CaptureMode.Rectangle, [source], []);
                    gesture.Begin(start, new(2, 2));
                    gesture.Move(end);
                    Equal(end, gesture.RawCurrent);
                    Point inside = new(x - (endsRight ? 1 : 0), y - (endsBottom ? 1 : 0));
                    frame.SetPixel(inside.X - source.X, inside.Y - source.Y, Color.Cyan);
                    using var image = new Bitmap(size, size);
                    using (Graphics graphics = Graphics.FromImage(image))
                        CaptureMagnifier.Draw(graphics, lens, frame, source, gesture.RawCurrent, gesture.Selection,
                            dotCount: dots, zoom: zoom);
                    var confirmed = gesture.End(end, OutputDestination.Clipboard)!;
                    Equal(selection, confirmed.Bounds);
                    using var cropped = CaptureGeometry.Crop(frame, source, confirmed.Bounds)!;
                    int inX = endsRight ? center - zoom / 2 : center + zoom / 2;
                    int inY = endsBottom ? center - zoom / 2 : center + zoom / 2;
                    Equal(cropped.GetPixel(endsRight ? cropped.Width - 1 : 0, endsBottom ? cropped.Height - 1 : 0).ToArgb(),
                        image.GetPixel(inX, inY).ToArgb());
                    Equal(Color.Cyan.ToArgb(), image.GetPixel(inX, inY).ToArgb());
                    Equal(Color.Red.ToArgb(), image.GetPixel(center, inY - 2).ToArgb());
                    Equal(Color.Red.ToArgb(), image.GetPixel(inX - 2, center).ToArgb());
                    Equal(Color.DodgerBlue.ToArgb(), image.GetPixel(center, inY).ToArgb());
                    Equal(Color.DodgerBlue.ToArgb(), image.GetPixel(inX, center).ToArgb());
                    int outX = 2 * center - inX, outY = 2 * center - inY;
                    foreach (Point outside in new Point[] { new(outX, outY), new(inX, outY), new(outX, inY) })
                    {
                        Color shaded = image.GetPixel(outside.X, outside.Y);
                        Check(shaded.G is > 0 and < 255 && shaded.B == 0, "撮影範囲外の象限が明るい");
                    }
                    frame.SetPixel(inside.X - source.X, inside.Y - source.Y, Color.Lime);
                }
        });
        Run("拡大鏡の初期値は20×20ドット・4倍で、保存済みの値を保持する", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            var fresh = store.Load(out string? warning);
            Equal<string?>(null, warning);
            Equal(20, fresh.MagnifierDotCount);
            Equal(4, fresh.MagnifierZoom);
            File.WriteAllText(path, """{"ShowMagnifier":true}""");
            AppSettings old = store.Load(out warning);
            Equal<string?>(null, warning);
            Equal(20, old.MagnifierDotCount);
            Equal(4, old.MagnifierZoom);
            store.Save(old with { MagnifierDotCount = 10, MagnifierZoom = 10 });
            var previousDefaults = store.Load(out warning);
            Equal<string?>(null, warning);
            Equal(10, previousDefaults.MagnifierDotCount);
            Equal(10, previousDefaults.MagnifierZoom);
            store.Save(old with { MagnifierDotCount = 18, MagnifierZoom = 7 });
            AppSettings current = store.Load(out warning);
            Equal<string?>(null, warning);
            Equal(18, current.MagnifierDotCount);
            Equal(7, current.MagnifierZoom);
            foreach (AppSettings invalid in new[]
            {
                current with { MagnifierDotCount = 9 }, current with { MagnifierDotCount = 34 },
                current with { MagnifierZoom = 1 }, current with { MagnifierZoom = 21 }
            })
                ExpectSettingsFailure<ArgumentException>(() => store.Save(invalid));
            Equal(18, store.Load(out _).MagnifierDotCount);
        }));
        Run("ウィンドウ・モニター境界とグリッドは選択を補正しても実ポインタを動かさない", () =>
        {
            Rectangle monitor = new(-400, -400, 400, 400), window = new(-288, -258, 100, 100);
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [monitor], [window]);
            gesture.Begin(new(-298, -240), new(2, 2), snap: true);
            Equal(new Point(-298, -240), gesture.RawCurrent);
            gesture.Move(new(-199, -167), snap: true);
            Equal(new Point(-188, -158), gesture.Current);
            Equal(new Rectangle(-288, -240, 100, 82), gesture.Selection);
            gesture.Move(new(-4, -1), snap: true);
            Equal(Point.Empty, gesture.Current);
            gesture.Move(new(-253, -191), action: CaptureKeyAction.Grid);
            Equal(new Point(-248, -200), gesture.Current);
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
            var larger = CaptureMagnifier.Place(viewport, center, [], previousQuadrant: 3, lensSize: 24 * 8);
            Equal(new Size(192, 192), larger.Bounds.Size);
            Check(viewport.Contains(larger.Bounds), "倍率変更後の拡大鏡が画面外");
            Equal(Rectangle.Empty, CaptureMagnifier.Place(viewport, center, [], -1, lensSize: 640).Bounds);
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
            var placement = CaptureMagnifier.Place(viewport, pointer, labels, -1, lensSize: 90);
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
                Equal(first.Bounds, session.SelectionBasis);
                Check(session.PointerHint!.EndsWith("座標（0, 8　24 × 14）\n各辺 +2 px"), "適用済みの余白と表示が合わない");
                session.MouseWheel(point, 120);
                Equal(Rectangle.Inflate(first.Bounds, 2, 2), session.MagnifierSelection);
                session.MouseWheel(point, -120);
                Equal(Rectangle.Inflate(first.Bounds, 1, 1), session.DisplaySelection);
                session.MoveTo(point + new Size(1, 0));
                Check(session.PointerHint!.EndsWith("各辺 +1 px"), "同一対象で余白を失った");
                session.MouseWheel(point, -120);
                Check(session.PointerHint!.EndsWith("0 px"), "元の枠へ戻っていない");
                session.MouseWheel(point, -1200);
                Equal(Rectangle.Inflate(first.Bounds, -4, -4), session.DisplaySelection);
                session.MouseWheel(point, 60);
                session.MoveTo(other.Bounds.Location);
                session.MouseWheel(other.Bounds.Location, 60);
                Equal<string?>(null, session.SelectionMarginHint);
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
                OutputDestination.Clipboard, FreezeFrame: false) { SelectionOnly = true },
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

    private static void SelectionFrameTests()
    {
        Run("青い基準枠と赤い撮影枠、暗転と拡大鏡は配信用描画でも実際の枠と一致する", () =>
        {
            Rectangle desktop = new(-200, -110, 400, 300), basis = new(-120, -60, 140, 100);
            Point point = basis.Location + new Size(38, 8);
            Color reference = Color.FromArgb(55, 195, 255);
            foreach (bool dim in new[] { false, true })
            {
                var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]);
                using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.White);
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    ShowSelectionGuide: false, DimCaptureBackground: dim,
                    OutputSettings: new() { ShowPointerGuide = dim }), snapshot, [basis]);
                using var overlay = new CaptureOverlay(session, desktop);
                try
                {
                    BeginOpticsPress(session, point);
                    session.MouseWheel(point, -720);
                    Rectangle adjusted = Rectangle.Inflate(basis, -6, -6);
                    Equal(adjusted, session.MagnifierSelection);
                    object visual = ReadOpticsVisual(overlay);
                    Equal(dim ? "基準 140 × 100" : "128 × 88", VisualProperty<string>(visual, "DimensionsText"));
                    string? hint = VisualProperty<string>(visual, "HintText");
                    Check(dim ? hint?.Contains("128 × 88", StringComparison.Ordinal) == true : hint is null, "ポインタガイドの寸法表示が一致しない");
                    foreach (bool shared in new[] { false, true })
                    {
                        using Bitmap image = DrawOpticsOverlay(overlay, visual, shared);
                        int solid = Enumerable.Range(92, 80).Count(x => image.GetPixel(x, 50).ToArgb() == reference.ToArgb());
                        Equal(80, solid);
                        int dashed = Enumerable.Range(92, 80).Count(x => image.GetPixel(x, 56).ToArgb() == Color.Red.ToArgb());
                        Check(dashed is > 0 and < 80, "撮影枠が赤い破線でない");
                        Equal(Color.White.ToArgb(), image.GetPixel(90, 80).ToArgb());
                        Equal(dim ? 140 : 255, (int)image.GetPixel(82, 80).R);
                    }
                    session.MouseWheel(point, 720);
                    using Bitmap reset = DrawOpticsOverlay(overlay, ReadOpticsVisual(overlay), shared: false);
                    Check(Enumerable.Range(92, 80).All(x => reset.GetPixel(x, 50).ToArgb() == Color.Red.ToArgb()),
                        "一致した枠が赤い実線に戻らない");
                    Equal(Color.White.ToArgb(), reset.GetPixel(92, 56).ToArgb());
                    session.MouseWheel(point, -720);
                    session.ChangeMagnifierVisibility(true);
                    visual = ReadOpticsVisual(overlay);
                    Rectangle lens = VisualProperty<Rectangle>(visual, "Magnifier");
                    Check(!lens.IsEmpty, "拡大鏡の表示領域を確保できない");
                    int redY = lens.Top + (adjusted.Top - point.Y + CaptureMagnifier.DefaultDotCount / 2) * CaptureMagnifier.DefaultZoom;
                    using Bitmap magnified = DrawOpticsOverlay(overlay, visual, shared: true);
                    Equal(Color.Red.ToArgb(), magnified.GetPixel(lens.Left + lens.Width / 2 + 1, redY).ToArgb());
                }
                finally { session.Cancel(); }
            }
        });
        Run("単押しは押下時の対象を使い、ホイールOFFとEsc・次回の端数リセットを尊重する", () =>
        {
            Rectangle desktop = new(0, 0, 200, 150), first = new(30, 20, 40, 40), second = new(70, 20, 50, 40);
            foreach (bool enabled in new[] { false, true })
            {
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot,
                    OutputDestination.Clipboard, OutputSettings: new() { CaptureKeys = new(WheelAdjustRectangle: enabled) }),
                    new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]), [first, second]);
                try
                {
                    Point down = new(first.Right - 1, 30), across = new(second.Left, 30);
                    BeginOpticsPress(session, down);
                    session.MouseWheel(across, 180);
                    Equal(first, session.SelectionBasis);
                    Equal(enabled ? Rectangle.Inflate(first, 1, 1) : first, session.DisplaySelection);
                    Check(session.HandleCommandKey(Keys.Escape, false), "Escを処理しない");
                    session.MouseUp(MouseButtons.Left, across, Keys.None);
                    Equal(Rectangle.Empty, session.DisplaySelection);
                    BeginOpticsPress(session, across);
                    session.MouseWheel(across, 60);
                    Equal(second, session.DisplaySelection);
                }
                finally { session.Cancel(); }
            }
        });
        Run("矩形ドラッグと2点クリックは基準枠を保ち、調整後の四隅を取得する", () =>
        {
            Rectangle desktop = new(0, 0, 200, 150), basis = new(30, 20, 70, 60);
            Point end = new(basis.Right, basis.Bottom);
            foreach (var method in Enum.GetValues<CaptureSelectionMethod>())
            {
                var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]);
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    OutputSettings: new() { SelectionMethod = method, CaptureKeys = new(SpaceEnabled: true) }), snapshot, [basis]);
                try
                {
                    if (method == CaptureSelectionMethod.Drag)
                    {
                        BeginOpticsPress(session, basis.Location);
                        session.MouseWheel(basis.Location, 420);
                    }
                    else
                    {
                        SetOpticsField(session, "pressedButton", MouseButtons.Left);
                        session.MouseUp(MouseButtons.Left, basis.Location, Keys.None);
                    }
                    session.MoveTo(end);
                    Equal(basis, session.DisplaySelection);
                    session.MouseWheel(end, -240);
                    Rectangle adjusted = Rectangle.Inflate(basis, -2, -2);
                    Equal(basis, session.SelectionBasis);
                    Equal(adjusted, session.DisplaySelection);
                    Check(session.HandleCommandKey(Keys.Space, false), "Spaceで基点が変わらない");
                    Equal(basis, session.SelectionBasis);
                    Equal(adjusted, session.MagnifierSelection);
                    (Point Pixel, Color Color)[] corners =
                    [
                        (adjusted.Location, Color.Red), (new(adjusted.Right - 1, adjusted.Top), Color.Blue),
                        (new(adjusted.Left, adjusted.Bottom - 1), Color.Lime), (new(adjusted.Right - 1, adjusted.Bottom - 1), Color.Magenta)
                    ];
                    foreach (var (pixel, color) in corners) snapshot.Image.SetPixel(pixel.X, pixel.Y, color);
                    SetOpticsField(session, "pressedButton", MouseButtons.Left);
                    session.MouseUp(MouseButtons.Left, session.Pointer, Keys.None);
                    Check(session.Completion.IsCompletedSuccessfully, "矩形選択を確定できない");
                    using var outcome = session.Completion.Result;
                    Equal<Point?>(null, outcome.Selection!.WindowHitPoint);
                    Equal(adjusted, outcome.Selection.Bounds);
                    foreach (var (pixel, color) in corners)
                        Equal(color.ToArgb(), outcome.Image!.GetPixel(pixel.X - adjusted.Left, pixel.Y - adjusted.Top).ToArgb());
                }
                finally { session.Cancel(); }
            }
        });
        Run("ライブ取得では対象移動後も調整し、前後のモニターを再描画して新しい画素を取得する", () =>
        {
            Rectangle[] monitors = [new(-160, 0, 160, 140), new(0, 0, 160, 140)];
            Rectangle desktop = Rectangle.Union(monitors[0], monitors[1]), basis = new(-80, 30, 50, 40);
            Point point = basis.Location + new Size(10, 12);
            var target = new CaptureWindow(basis, "対象", 1, 0, 0);
            DesktopSnapshot NewSnapshot() => new(new Bitmap(desktop.Width, desktop.Height), desktop, monitors);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                FreezeFrame: false, LimitToStartingMonitor: false, ShowSelectionGuide: false,
                OutputSettings: new() { ShareCapturePresentation = true }),
                NewSnapshot(), [basis], findWindow: p => basis.Contains(p) ? target : null, currentFrameFactory: () =>
                {
                    var current = NewSnapshot();
                    Rectangle adjusted = Rectangle.Inflate(target.Bounds, 3, 3);
                    current.Image.SetPixel(adjusted.Left - desktop.Left, adjusted.Top, Color.Magenta);
                    return current;
                });
            using var oldScreen = new CaptureOverlay(session, monitors[0]);
            using var newScreen = new CaptureOverlay(session, monitors[1]);
            try
            {
                BeginOpticsPress(session, point);
                session.MouseWheel(point, 360);
                Equal(Rectangle.Inflate(basis, 3, 3), session.DisplaySelection);
                oldScreen.InvalidateChanges();
                newScreen.InvalidateChanges();
                target = target with { Bounds = new(110, 35, 50, 40) };
                OpticsMethod(typeof(SessionController), "RefreshLiveWindowMargin").Invoke(session, null);
                Equal(target.Bounds, session.DisplaySelection);
                Check(oldScreen.InvalidateChanges() && newScreen.InvalidateChanges(), "移動前後のモニターを更新しない");
                target = target with { Bounds = new(20, 35, 50, 40) };
                OpticsMethod(typeof(SessionController), "RefreshLiveWindowMargin").Invoke(session, null);
                Rectangle expected = Rectangle.Inflate(target.Bounds, 3, 3);
                Equal(target.Bounds, session.SelectionBasis);
                Equal(expected, session.MagnifierSelection);
                session.MouseUp(MouseButtons.Left, point, Keys.None);
                Check(session.Completion.IsCompletedSuccessfully, "移動後のウィンドウを確定できない");
                using var outcome = session.Completion.Result;
                Equal(expected, outcome.Selection!.Bounds);
                Equal(Color.Magenta.ToArgb(), outcome.Image!.GetPixel(0, 0).ToArgb());
            }
            finally { session.Cancel(); }
        });
        Run("固定枠と固定位置、単一コントロール、WGCにも共通の基準枠を使う", () =>
        {
            Rectangle desktop = new(0, 0, 240, 200), basis = new(30, 20, 70, 60);
            Point point = basis.Location + new Size(6, 6);
            DesktopSnapshot NewSnapshot() => new(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]);
            foreach (bool registered in new[] { false, true })
            {
                var fixedSession = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot,
                    OutputDestination.Clipboard, FixedSize: basis.Size), NewSnapshot(), []);
                try
                {
                    if (registered) OpticsGesture(fixedSession).FixedPosition = basis;
                    fixedSession.MouseWheel(basis.Location, 120);
                    Equal(basis, fixedSession.SelectionBasis);
                    Equal(Rectangle.Inflate(basis, 1, 1), fixedSession.DisplaySelection);
                    Equal(basis.Location, fixedSession.MagnifierSample);
                    Equal("各辺 +1 px", fixedSession.SelectionMarginHint);
                    fixedSession.MouseWheel(basis.Location, -120);
                    Equal("0 px", fixedSession.SelectionMarginHint);
                }
                finally { fixedSession.Cancel(); }
            }
            var target = new CaptureWindow(basis, "対象", 1, 0, 0);
            var control = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot,
                OutputDestination.Clipboard), NewSnapshot(), [basis], findWindow: _ => target);
            try
            {
                Rectangle part = Rectangle.Inflate(basis, -3, -3);
                SetOpticsField(control, "controlToggled", true);
                SetOpticsField(control, "selectingControl", true);
                SetOpticsField(control, "controlRequest", (point, target));
                SetOpticsField(control, "controlCandidate", (target, part, (ControlIdentity?)null));
                control.MouseWheel(point, 120);
                Equal(part, control.SelectionBasis);
                Equal(Rectangle.Inflate(part, 1, 1), control.MagnifierSelection);
            }
            finally { control.Cancel(); }
            var window = new SessionController(new CaptureOptions(CaptureMode.Window, CaptureIntent.Screenshot,
                OutputDestination.Clipboard) { SelectionOnly = true, RestartState = new(false, true, CaptureAspectRatio.Free, null) },
                NewSnapshot(), [basis], findWindow: _ => target);
            var preview = new WindowCapturePreview(target, false, [target]);
            var frame = new Bitmap(basis.Width, basis.Height);
            frame.SetPixel(1, 1, Color.Lime);
            typeof(WindowCapturePreview).GetField("frame", OpticsPrivate)!.SetValue(preview,
                Task.FromResult(new WindowCaptureFrame(frame, basis, DateTimeOffset.Now)));
            SetOpticsField(window, "windowPreview", preview);
            try
            {
                window.MouseWheel(point, 960);
                Equal(basis, window.SelectionBasis);
                Equal(basis, window.DisplaySelection);
                window.MouseWheel(point, -120);
                Rectangle expected = Rectangle.Inflate(basis, -1, -1);
                Equal(basis, window.SelectionBasis);
                Equal(expected, window.MagnifierSelection);
                using Bitmap cropped = CaptureTargetMargin.CropFrame(frame, basis, expected, CaptureGeometry.MinimumSize);
                Equal(Color.Lime.ToArgb(), cropped.GetPixel(0, 0).ToArgb());
            }
            finally { window.Cancel(); }
        });
    }

    private const BindingFlags OpticsPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void CrosshairGuideTests()
    {
        Run("矩形の全画面ガイド線は独立し1点目・ドラッグ・2点クリックで実ポインタを追う", () =>
        {
            Rectangle left = new(-100, -20, 100, 80), right = new(0, -20, 140, 80);
            Rectangle desktop = Rectangle.Union(left, right);
            foreach (var method in Enum.GetValues<CaptureSelectionMethod>())
            {
                using var bitmap = new Bitmap(desktop.Width, desktop.Height);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Teal);
                var options = new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    LimitToStartingMonitor: false, ShowSelectionGuide: false, OutputSettings: new AppSettings
                    {
                        ShowCrosshairGuide = true, ShowPointerGuide = false, ShowMagnifier = false, SelectionMethod = method,
                        ShowSelectionGuide = false
                    });
                using var snapshot = new DesktopSnapshot((Bitmap)bitmap.Clone(), desktop, [left, right]);
                var session = new SessionController(options, snapshot, [], initialPointer: new(-70, 5));
                using var a = new CaptureOverlay(session, left);
                using var b = new CaptureOverlay(session, right);
                try
                {
                    var start = new Point(-70, 5);
                    session.MoveTo(start);
                    var visual = ReadOpticsVisual(a);
                    Equal<Point?>(new(30, 25), VisualProperty<Point?>(visual, "Crosshair"));
                    Equal<Point?>(null, VisualProperty<Point?>(ReadOpticsVisual(b), "Crosshair"));
                    a.InvalidateChanges();
                    b.InvalidateChanges();
                    foreach (bool shared in new[] { false, true })
                    {
                        using var image = DrawOpticsOverlay(a, visual, shared);
                        foreach (Point pixel in new Point[] { new(0, 25), new(99, 25), new(30, 0), new(30, 79) })
                            Equal(Color.White.ToArgb(), image.GetPixel(pixel.X, pixel.Y).ToArgb());
                        Equal(Color.Black.ToArgb(), image.GetPixel(0, 24).ToArgb());
                    }
                    session.MouseDown(a, MouseButtons.Left, start);
                    if (method == CaptureSelectionMethod.TwoClicks) session.MouseUp(MouseButtons.Left, start);
                    Point end = new(24, 30);
                    session.MoveTo(end);
                    Check(a.InvalidateChanges() && b.InvalidateChanges(), "両方のモニターを更新しない");
                    Equal<Point?>(null, VisualProperty<Point?>(ReadOpticsVisual(a), "Crosshair"));
                    var endVisual = ReadOpticsVisual(b);
                    Equal<Point?>(new(24, 50), VisualProperty<Point?>(endVisual, "Crosshair"));
                    foreach (bool shared in new[] { false, true })
                    {
                        using var image = DrawOpticsOverlay(b, endVisual, shared);
                        Equal(Color.Red.ToArgb(), image.GetPixel(24, 45).ToArgb());
                        Equal(Color.Red.ToArgb(), image.GetPixel(12, 50).ToArgb());
                        Equal(Color.White.ToArgb(), image.GetPixel(60, 50).ToArgb());
                    }
                    if (method == CaptureSelectionMethod.TwoClicks) session.MouseDown(b, MouseButtons.Left, end);
                    session.MouseUp(MouseButtons.Left, end);
                    using var outcome = session.Completion.GetAwaiter().GetResult();
                    for (int y = 0; y < outcome.Image!.Height; y++)
                        for (int x = 0; x < outcome.Image.Width; x++)
                            Equal(Color.Teal.ToArgb(), outcome.Image.GetPixel(x, y).ToArgb());
                }
                finally { session.Cancel(); }
            }
            var (horizontal, vertical) = CaptureOverlay.CrosshairBands(new(0, 0, 140, 80), Point.Empty);
            Equal(new Rectangle(0, 0, 140, 2), horizontal);
            Equal(new Rectangle(0, 0, 2, 80), vertical);
        });
    }

    private static MethodInfo OpticsMethod(Type type, string name) => type.GetMethod(name, OpticsPrivate)!;

    private static object ReadOpticsVisual(CaptureOverlay overlay) =>
        OpticsMethod(typeof(CaptureOverlay), "ReadVisualState").Invoke(overlay, null)!;

    private static T? VisualProperty<T>(object state, string name) =>
        (T?)state.GetType().GetProperty(name)!.GetValue(state);

    private static Bitmap DrawOpticsOverlay(CaptureOverlay overlay, object state, bool shared)
    {
        var image = new Bitmap(overlay.MonitorBounds.Width, overlay.MonitorBounds.Height);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(Color.White);
        OpticsMethod(typeof(CaptureOverlay), "DrawForeground").Invoke(overlay,
            [graphics, new Rectangle(Point.Empty, image.Size), shared, state]);
        return image;
    }

    private static void SetOpticsField(SessionController session, string name, object value) =>
        typeof(SessionController).GetField(name, OpticsPrivate)!.SetValue(session, value);

    private static SelectionGesture OpticsGesture(SessionController session) =>
        (SelectionGesture)typeof(SessionController).GetField("gesture", OpticsPrivate)!.GetValue(session)!;

    private static void BeginOpticsPress(SessionController session, Point point)
    {
        SetOpticsField(session, "pressedButton", MouseButtons.Left);
        OpticsGesture(session).Begin(point, new(8, 8));
        session.MoveTo(point);
    }
}
