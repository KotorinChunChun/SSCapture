using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureCompositeTests()
    {
        Run("WGCの付随ダイアログが閉じても明示選択した親の画像を保持する", () =>
        {
            CaptureWindow parent = new(new(10, 10, 80, 80), "親", 1, 10, 10);
            CaptureWindow child = new(new(20, 20, 40, 40), "子", 2, 10, 10, parent.Handle);
            Task<WindowCaptureFrame> Read(CaptureWindow window, bool _, CancellationToken __) => window == child
                ? Task.FromException<WindowCaptureFrame>(new InvalidOperationException("閉じたウィンドウ"))
                : Task.FromResult(new WindowCaptureFrame(new Bitmap(80, 80), parent.Bounds, default));
            var captured = IsolatedWindowCapture.TakeGroupAsync([child, parent], false, required: new HashSet<nint> { parent.Handle }, takeFrame: Read).GetAwaiter().GetResult();
            using var bitmap = captured.Image;
            Equal(parent.Bounds, captured.Bounds);
        });
        Run("モニター合成は負座標・隙間・通らなかったモニターを区別し、再撮影にも対象を引き継ぐ", () =>
        {
            Rectangle left = new(-200, -80, 120, 110), right = new(0, -40, 170, 80), skipped = new(-80, -80, 80, 25);
            Rectangle canvas = Rectangle.Union(left, right);
            var drag = new CaptureTargetDrag();
            drag.Move(new(-100, 0), [left, right, skipped], null);
            drag.Move(new(8, 0), [left, right, skipped], null);
            drag.Move(new(-100, 0), [left, right, skipped], null);
            Check(drag.Monitors.SetEquals([left, right]), "通過していないモニターを含めたか、通過済みの対象を忘れた");

            using var source = new Bitmap(canvas.Width, canvas.Height);
            using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.Blue);
            using var image = CaptureGeometry.ComposeRegions(source, canvas, canvas, drag.Monitors.ToArray());
            Equal(Color.Blue.ToArgb(), image.GetPixel(10, 10).ToArgb());
            Equal(Color.Blue.ToArgb(), image.GetPixel(230, 50).ToArgb());
            Equal(0, image.GetPixel(150, 10).A);
            Equal(0, image.GetPixel(150, 50).A);

            DisplayLayout[] layout = [new("左", "左", left, 96, 96), new("右", "右", right, 96, 96), new("上", "上", skipped, 96, 96)];
            LastCaptureRecipe recipe = LastCaptureRecipe.Create(1, canvas, null, new AppSettings(), layout,
                new CaptureReplaySource.Monitors([left, right]));
            Rectangle movedRight = new(100, 50, right.Width + 60, right.Height + 20);
            DisplayLayout[] rearranged = [layout[0], layout[1] with { Bounds = movedRight }, layout[2]];
            Check(recipe.RegionsFor(rearranged)!.SequenceEqual([left, movedRight]), "再撮影に異なるモニターが混ざった");
        });
        Run("前面に見える所有ウィンドウを含め、他アプリと完全に隠れた子を除外する", () =>
        {
            CaptureWindow root = new(new(0, 0, 300, 300), "親", 1, 10, 10, ZOrder: 4);
            CaptureWindow child = new(new(120, 100, 80, 60), "子", 2, 10, 11, root.Handle, 1);
            CaptureWindow covered = new(new(20, 20, 60, 60), "隠れた子", 3, 10, 11, root.Handle, 2);
            CaptureWindow other = new(covered.Bounds, "他アプリ", 4, 20, 20, ZOrder: 0);
            CaptureWindow unrelated = new(new(250, 250, 60, 60), "同プロセスの別画面", 5, 10, 11, ZOrder: 3);
            Check(CaptureWindowGroups.ForegroundGroup(root, [other, child, covered, unrelated, root]).SequenceEqual([child, root]),
                "前面の所有ウィンドウを正しく識別しない");

            using var back = new Bitmap(300, 300);
            using var front = new Bitmap(80, 60);
            using (var graphics = Graphics.FromImage(back)) graphics.Clear(Color.White);
            using (var graphics = Graphics.FromImage(front)) graphics.Clear(Color.Orange);
            using var composed = CaptureGeometry.ComposeWindows(root.Bounds, [new(front, child.Bounds, default), new(back, root.Bounds, default)]);
            Equal(Color.White.ToArgb(), composed.GetPixel(100, 100).ToArgb());
            Equal(Color.Orange.ToArgb(), composed.GetPixel(150, 120).ToArgb());
        });
        Run("コントロールの軌跡は移動イベント間と負座標の1pxを通過し、重複・経路外・背面を除外する", () =>
        {
            CaptureWindow left = new(new(-100, -30, 90, 100), "左", 1, 10, 10);
            CaptureWindow right = new(new(-20, -30, 100, 100), "右", 2, 20, 20, ZOrder: 1);
            Rectangle thin = new(-40, 0, 1, 20), behind = new(-20, 0, 8, 20), last = new(10, 0, 20, 20);
            var drag = new CaptureControlDrag(new(-60, 10));
            CaptureWindow? Find(Point point) => left.Bounds.Contains(point) ? left : right.Bounds.Contains(point) ? right : null;
            drag.Move(new(50, 10), Find);
            drag.Move(new(-60, 10), Find);
            Rectangle[] monitors = [Rectangle.Union(left.Bounds, right.Bounds)];
            Equal(left, drag.NextWindow);
            drag.SetLayout(left, left, new([Entry(thin), Entry(new(-40, 30, 20, 20))]), monitors);
            Equal(right, drag.NextWindow);
            drag.SetLayout(right, right, new([Entry(last), Entry(behind)]), monitors);
            drag.Move(new(20, 10), Find);
            Check(drag.Selected.Select(target => target.Bounds).ToHashSet().SetEquals([thin, last]), "通過対象が抜けたか余分な対象を含めた");
            Equal(2, drag.Selected.Count);
            Equal(new Rectangle(-60, 10, 111, 1), drag.TrailBounds);

            static ControlLayoutEntry Entry(Rectangle r) => new(new(PixelBounds.From(r), null), PixelBounds.From(r), true, 0);
        });
        Run("WGCのコントロール合成は同じ親を一度撮影し、選択外は透過して重なりは前面を優先する", () =>
        {
            var back = new CaptureWindow(new(-40, -20, 80, 70), "背面", 11, 10, 10, ZOrder: 3);
            var front = new CaptureWindow(new(0, -20, 80, 70), "前面", 12, 10, 10, ZOrder: 2);
            ControlIdentity id = new("ボタン", 50000);
            CaptureControlTarget[] targets = [new(back, new(-20, 0, 10, 10), id),
                new(back, new(-4, 3, 18, 12), new("別のボタン", 50000)), new(front, new(3, 9, 28, 12), id)];
            List<nint> fetched = [];
            Task<WindowCaptureFrame> Read(CaptureWindow window, bool _, CancellationToken cancellationToken)
            {
                fetched.Add(window.Handle);
                var frame = new Bitmap(window.Bounds.Width, window.Bounds.Height);
                using (var painter = Graphics.FromImage(frame)) painter.Clear(window == back ? Color.Blue : Color.Red);
                return Task.FromResult(new WindowCaptureFrame(frame, window.Bounds, default));
            }

            var capture = IsolatedWindowCapture.TakeControlsAsync(targets, new AppSettings(), takeFrame: Read).GetAwaiter().GetResult();
            using var image = capture.Image;
            Check(fetched.SequenceEqual([back.Handle, front.Handle]), "ウィンドウの描画順か取得回数が合わない");
            Equal(new Size(51, 21), image.Size);
            Equal(Color.Blue.ToArgb(), image.GetPixel(1, 1).ToArgb());
            Equal(0, image.GetPixel(13, 3).A);
            Equal(Color.Blue.ToArgb(), image.GetPixel(19, 10).ToArgb());
            Equal(Color.Red.ToArgb(), image.GetPixel(25, 10).ToArgb());
            Equal(0, image.GetPixel(49, 2).A);

            var union = targets.Select(target => target.Bounds).Aggregate(Rectangle.Union);
            var recipe = LastCaptureRecipe.Create(1, union, null, new AppSettings(), [],
                new CaptureReplaySource.ControlGroup(targets, true), _ => 22);
            Check(recipe.TracksWindows && recipe.UseIsolatedWindow, "親ウィンドウまたはWGC設定を再撮影に引き継がない");
            Check(recipe.ControlGroup!.SequenceEqual([(0, id), (0, targets[1].Identity), (1, id)]), "コントロールの親と識別子の関連付けが誤っている");
            Check(recipe.ResolveWindows([front, back], _ => 22).SequenceEqual([back, front]), "再撮影の親インデックスが崩れた");
        });
        Run("高速な左ドラッグでも途中のウィンドウを拾い、往復しても重複しない", () =>
        {
            CaptureWindow first = new(new(0, 0, 30, 20), "左", 1, 10, 10);
            CaptureWindow thin = new(new(32, 0, 2, 20), "中", 2, 10, 10);
            CaptureWindow last = new(new(40, 0, 40, 20), "右", 3, 20, 20);
            CaptureWindow[] windows = [first, thin, last];
            var owned = first with { Bounds = new Rectangle(1, 30, 20, 20), Handle = 4, Owner = first.Handle };
            var drag = new CaptureTargetDrag(window => window == first ? [owned, window] : [window]);
            CaptureWindow? Find(Point point) => windows.FirstOrDefault(window => window.Bounds.Contains(point));
            drag.Move(new(10, 10), [], Find);
            drag.Move(new(70, 10), [], Find);
            drag.Move(new(10, 10), [], Find);
            Check(drag.Windows.SequenceEqual(windows), "途中の対象が抜けるか重複した");
            Check(drag.BrightWindows.ToHashSet().SetEquals([first.Bounds, owned.Bounds, thin.Bounds, last.Bounds]),
                "選択済みのウィンドウまたは前面の所有ウィンドウの暗転が残る");
            Equal(new Rectangle(10, 10, 61, 1), drag.TrailBounds);
        });
    }
}
