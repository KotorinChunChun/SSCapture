using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureCompositeTests()
    {
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
                composite: new(Regions: [left, right]));
            Rectangle movedRight = new(100, 50, right.Width, right.Height);
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
