using SSCapture.Capture;
using SSCapture.UI;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void SnapTests()
    {
        Run("モニタ角ぴったりの吸着成立は座標差ゼロでも刻みより優先する", () =>
        {
            foreach (var monitor in new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(-2560, -1440, 2560, 1440), new Rectangle(-1920, 200, 1920, 1080) })
            foreach (var target in new[] { monitor.Location, new Point(monitor.Right, monitor.Top), new Point(monitor.Left, monitor.Bottom), new Point(monitor.Right, monitor.Bottom) })
            foreach (bool alt in new[] { false, true })
            {
                var gesture = new SelectionGesture(CaptureMode.Rectangle, [monitor], []) { Grid20 = true };
                var origin = new Point(target.X == monitor.Left ? monitor.Right - 15 : monitor.Left + 15,
                    target.Y == monitor.Top ? monitor.Bottom - 15 : monitor.Top + 15);
                gesture.Begin(origin, new Size(1, 1)); gesture.Move(target, alt);
                Equal(target, gesture.Current);
            }
            Check(!new AppSettings().Grid20, "新規の刻み調整がON");
        });
        Run("モニタ境界はより近いウィンドウ境界にも優先しAltなしではウィンドウ吸着しない", () =>
        {
            var snap = new SelectionSnap([new(0, 0, 1920, 1080)], [new(5, 5, 400, 300)]);
            Equal(new Point(0, 0), snap.Apply(new Point(5, 5)));
            var result = snap.Evaluate(Point.Empty); Check(result.X && result.Y, "一致する辺の成立を失った");
            Equal(new Point(400, 200), snap.Evaluate(new Point(400, 200), includeWindows: false).Point);
        });
        Run("スナップはウィンドウ辺の20物理pxまで、21pxは補正しない", () =>
        {
            var snap = new SelectionSnap([], [new Rectangle(100, 100, 200, 160)]);
            Equal(new Point(100, 170), snap.Apply(new Point(120, 170)));
            Equal(new Point(121, 170), snap.Apply(new Point(121, 170)));
            Equal(new Point(180, 100), snap.Apply(new Point(180, 120)));
            Equal(new Point(180, 121), snap.Apply(new Point(180, 121)));
            Equal(new Point(300, 170), snap.Apply(new Point(320, 170)));
            Equal(new Point(321, 170), snap.Apply(new Point(321, 170)));
            Equal(new Point(180, 260), snap.Apply(new Point(180, 280)));
            Equal(new Point(180, 281), snap.Apply(new Point(180, 281)));
        });
        Run("スナップは有限線分と角への距離を使い遠い延長線へ寄せない", () =>
        {
            var snap = new SelectionSnap([], [new Rectangle(100, 100, 200, 160)]);
            Equal(new Point(110, 40), snap.Apply(new Point(110, 40)));
            Equal(new Point(40, 110), snap.Apply(new Point(40, 110)));
            Equal(new Point(100, 100), snap.Apply(new Point(86, 86)));
            Equal(new Point(85, 85), snap.Apply(new Point(85, 85)));
            Equal(new Point(100, 100), snap.Apply(new Point(108, 109)));
            Equal(new Point(300, 260), snap.Apply(new Point(292, 251)));
        });
        Run("スナップは負座標と異なる物理画面寸法でも20pxを維持", () =>
        {
            var snap = new SelectionSnap([new(-1920, -200, 1920, 1080), new(0, 0, 2560, 1440)], []);
            Equal(new Point(-1920, 400), snap.Apply(new Point(-1900, 400)));
            Equal(new Point(-1899, 400), snap.Apply(new Point(-1899, 400)));
            Equal(new Point(0, 400), snap.Apply(new Point(20, 400)));
            Equal(new Point(21, 400), snap.Apply(new Point(21, 400)));
            Equal(new Point(2560, 400), snap.Apply(new Point(2540, 400)));
            Equal(new Point(2539, 400), snap.Apply(new Point(2539, 400)));
        });
        Run("スナップは複数ウィンドウの最も近い辺を優先する", () =>
        {
            Rectangle[] windows = [new(100, 100, 300, 300), new(125, 100, 300, 300)];
            Equal(new Point(125, 200), new SelectionSnap([], windows).Apply(new Point(117, 200)));
            Equal(new Point(125, 200), new SelectionSnap([], windows.Reverse().ToArray()).Apply(new Point(117, 200)));
        });
        Run("スナップ対象は選択開始時に凍結して極端な座標でもoverflowしない", () =>
        {
            Rectangle[] windows = [new(100, 100, 200, 160)];
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [], windows);
            windows[0] = new Rectangle(500, 500, 200, 160);
            gesture.Move(new Point(108, 109), true);
            Equal(new Point(100, 100), gesture.Current);
            var snap = new SelectionSnap([], [new(int.MinValue, 0, 100, 100), new(int.MaxValue - 10, 0, 100, 100)]);
            Equal(new Point(int.MaxValue, int.MinValue), snap.Apply(new Point(int.MaxValue, int.MinValue)));
            Equal(new Point(int.MinValue, 50), snap.Apply(new Point(int.MinValue + 20, 50)));
        });
        Run("矩形はAlt開始点を固定して終了点のAlt解除だけrawへ戻す", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], [new(100, 100, 220, 160)]);
            gesture.Begin(new Point(108, 109), new Size(8, 8), true);
            gesture.Move(new Point(312, 251), true);
            Equal(new Rectangle(100, 100, 220, 160), gesture.Selection);
            gesture.Move(new Point(312, 251), false);
            Equal(new Rectangle(100, 100, 212, 151), gesture.Selection);
            Equal(new Point(312, 251), gesture.RawCurrent);
            Equal(new Rectangle(100, 100, 220, 160), gesture.End(new Point(312, 251), OutputDestination.File, true)!.Bounds);
        });
        Run("矩形開始後のAltは始点を動かさず逆方向の終点にも適用する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], [new(100, 100, 220, 160)]);
            gesture.Begin(new Point(312, 251), new Size(8, 8));
            gesture.Move(new Point(108, 109), true);
            Equal(new Rectangle(100, 100, 212, 151), gesture.Selection);
            Equal(new Rectangle(100, 100, 212, 151), gesture.End(new Point(108, 109), OutputDestination.Clipboard, true)!.Bounds);
        });
        Run("Alt切替だけをドラッグにせずクリック対象はraw地点のウィンドウ", () =>
        {
            Rectangle front = new(100, 100, 200, 160), behind = new(0, 0, 800, 600);
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [behind], [front, behind]);
            Point raw = new(94, 150);
            gesture.Begin(raw, new Size(8, 8), true);
            Equal(new Point(100, 150), gesture.Current);
            gesture.Move(raw, false); gesture.Move(raw, true);
            Check(!gesture.HasDragged, "Altだけでドラッグ扱いになった");
            Equal(behind, gesture.End(raw, OutputDestination.File, true)!.Bounds);
            gesture.Begin(raw, new Size(8, 8), true);
            Equal(behind, gesture.End(new Point(96, 151), OutputDestination.File, false)!.Bounds);
        });
        Run("自由形状とウィンドウ選択はAltで座標補正しない", () =>
        {
            Rectangle window = new(100, 100, 220, 160);
            foreach (CaptureMode mode in new[] { CaptureMode.Freeform, CaptureMode.Window })
            {
                var gesture = new SelectionGesture(mode, [new(0, 0, 1000, 1000)], [window]);
                gesture.Begin(new Point(108, 109), new Size(8, 8), true);
                Equal(new Point(108, 109), gesture.Current);
                gesture.Move(new Point(312, 251), true);
                Equal(new Point(312, 251), gesture.Current);
                if (mode == CaptureMode.Window) Equal(window, gesture.Selection);
                else Check(gesture.Points.SequenceEqual(new[] { new Point(108, 109), new Point(312, 251) }), "自由形状の点を補正した");
            }
        });
        foreach (CaptureAnchor anchor in Enum.GetValues<CaptureAnchor>())
        {
            Run($"固定{anchor}基点は負座標でも寸法を保ち矢印とクリックを両立", () =>
            {
                Size size = new(320, 180);
                Point pointer = new(-600, -200);
                int dx = anchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? size.Width : 0;
                int dy = anchor is CaptureAnchor.BottomLeft or CaptureAnchor.BottomRight ? size.Height : 0;
                var gesture = new SelectionGesture(CaptureMode.FixedRegion, [new(-2000, -1000, 4000, 2000)], [], size, anchor);
                gesture.Move(pointer);
                Rectangle expected = new(pointer.X - dx, pointer.Y - dy, size.Width, size.Height);
                Equal(expected, gesture.Selection);
                Check(gesture.Nudge(Keys.Right), "1px移動を処理しない");
                Check(gesture.Nudge(Keys.Up | Keys.Shift), "10px移動を処理しない");
                expected.Offset(1, -10);
                gesture.Move(pointer);
                Equal(expected, gesture.Selection);
                gesture.Begin(pointer, new Size(8, 8));
                Equal(expected, gesture.End(pointer, OutputDestination.File)!.Bounds);
                Equal(size, gesture.Selection.Size); Equal(pointer, gesture.RawCurrent);
                gesture.Move(new Point(pointer.X + 2, pointer.Y));
                Equal(new Rectangle(pointer.X + 2 - dx, pointer.Y - dy, size.Width, size.Height), gesture.Selection);
            });
            Run($"固定{anchor}基点はAltで静止切替できサイズを変更しない", () =>
            {
                Size size = new(80, 60); Point raw = new(312, 251), snapped = new(320, 260);
                int dx = anchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? size.Width : 0;
                int dy = anchor is CaptureAnchor.BottomLeft or CaptureAnchor.BottomRight ? size.Height : 0;
                var gesture = new SelectionGesture(CaptureMode.FixedRegion, [new(0, 0, 1000, 1000)], [new(100, 100, 220, 160)], size, anchor);
                gesture.Move(raw, true);
                Equal(new Rectangle(snapped.X - dx, snapped.Y - dy, size.Width, size.Height), gesture.Selection);
                gesture.Move(raw, false);
                Equal(new Rectangle(raw.X - dx, raw.Y - dy, size.Width, size.Height), gesture.Selection);
                gesture.Begin(raw, new Size(8, 8), true);
                Equal(new Rectangle(snapped.X - dx, snapped.Y - dy, size.Width, size.Height), gesture.End(raw, OutputDestination.File, true)!.Bounds);
            });
        }
        Run("右下基点の矢印はint座標端でも寸法を保つ", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.FixedRegion, [], [], new Size(80, 60), CaptureAnchor.BottomRight);
            gesture.Move(new Point(int.MinValue + 80, int.MinValue + 60));
            gesture.Nudge(Keys.Left | Keys.Shift); gesture.Nudge(Keys.Up | Keys.Shift);
            Equal(new Rectangle(int.MinValue, int.MinValue, 80, 60), gesture.Selection);
            gesture.Move(new Point(int.MaxValue, int.MaxValue));
            gesture.Nudge(Keys.Right); gesture.Nudge(Keys.Down);
            Equal(new Rectangle(int.MaxValue - 80, int.MaxValue - 60, 80, 60), gesture.Selection);
        });
        Run("Sessionの合成Alt切替はOS入力なしで矢印調整とraw座標を維持", () =>
        {
            Rectangle desktop = new(-20, -10, 12, 12);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                FixedSize: new Size(2, 2), FixedAnchor: CaptureAnchor.BottomRight), new DesktopSnapshot(PatternBitmap(), desktop, [desktop]), []);
            try
            {
                Point raw = new(-10, 0);
                session.MoveTo(raw, true);
                Equal(new Rectangle(-10, 0, 2, 2), session.Selection);
                session.MoveTo(raw, false);
                Equal(new Rectangle(-12, -2, 2, 2), session.Selection);
                session.HandleCommandKey(Keys.Left);
                session.MoveTo(raw, false);
                Equal(new Rectangle(-13, -2, 2, 2), session.Selection);
                session.MoveTo(raw, true);
                Equal(new Rectangle(-10, 0, 2, 2), session.Selection);
            }
            finally { session.Cancel(); }
        });
        Run("選択ガイドは4隅基点とAlt説明を表示領域内に収める", () =>
        {
            foreach ((CaptureAnchor anchor, string label) in new[] { (CaptureAnchor.TopLeft, "左上"), (CaptureAnchor.TopRight, "右上"),
                (CaptureAnchor.BottomRight, "右下"), (CaptureAnchor.BottomLeft, "左下") })
            {
                using var guide = new SelectionGuideForm(new(0, 0, 1920, 1080), CaptureIntent.Screenshot, () => { },
                    new Size(1280, 720), fixedAnchor: anchor);
                guide.CreateControl(); LayoutTree(guide);
                var help = (Label)guide.Controls.Find("SelectionHelp", true).Single();
                Check(help.Text.Contains("基点: " + label, StringComparison.Ordinal) && help.Text.Contains("Alt", StringComparison.Ordinal), "基点かAltの説明が欠落");
                Size measured = TextRenderer.MeasureText(help.Text, help.Font,
                    new Size(help.Width - help.Padding.Horizontal, int.MaxValue), TextFormatFlags.WordBreak);
                Check(guide.ClientRectangle.Contains(help.Bounds) && measured.Height <= help.Height - help.Padding.Vertical, "ガイドの文字が領域外");
            }
        });
    }
}
