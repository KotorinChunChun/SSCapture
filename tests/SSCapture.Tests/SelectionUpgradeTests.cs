using SSCapture.Capture;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionUpgradeTests()
    {
        Run("矩形Spaceは生ポインタから始点を再設定し寸法とロックを捨てる", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], []);
            gesture.Begin(new(100, 100), new(8, 8)); gesture.Move(new(200, 180));
            gesture.Move(new(400, 350), lockHeight: true, lockWidth: true);
            Check(gesture.Space(), "Spaceを処理しない");
            Check(gesture.IsPressed && !gesture.HasDragged, "ドラッグを終了した");
            Equal(Rectangle.Empty, gesture.Selection);
            gesture.Move(new(500, 450));
            Equal(new Rectangle(400, 350, 100, 100), gesture.Selection);
            Equal(new Rectangle(400, 350, 100, 100), gesture.End(new(500, 450), OutputDestination.File)!.Bounds);
        });
        Run("Space直後に離すとウィンドウ撮影にならず選び直せる", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], [new(0, 0, 900, 900)]);
            Check(!gesture.Space(), "未ドラッグのSpaceを処理した");
            gesture.Begin(new(10, 10), new(8, 8)); gesture.Move(new(100, 100)); gesture.Space();
            Check(gesture.End(new(100, 100), OutputDestination.Clipboard) is null, "ウィンドウ撮影へ誤変換した");
            gesture.Begin(new(300, 300), new(8, 8)); gesture.Move(new(200, 200)); gesture.Space();
            gesture.Move(new(100, 120));
            Equal(new Rectangle(100, 120, 100, 80), gesture.Selection);
        });
        Run("固定Spaceは時計回り4隅で寸法とポインタを維持", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.FixedRegion, [new(-1000, -1000, 2000, 2000)], [], new(160, 90));
            Point pointer = new(100, 200); gesture.Move(pointer);
            foreach (var anchor in new[] { CaptureAnchor.TopRight, CaptureAnchor.BottomRight, CaptureAnchor.BottomLeft, CaptureAnchor.TopLeft })
            {
                Check(gesture.Space(), "Space未処理"); Equal(anchor, gesture.FixedAnchor);
                Equal(pointer, gesture.Current); Equal(new Size(160, 90), gesture.Selection.Size);
                Equal(anchor is CaptureAnchor.TopRight or CaptureAnchor.BottomRight ? -60 : 100, gesture.Selection.X);
                Equal(anchor is CaptureAnchor.BottomRight or CaptureAnchor.BottomLeft ? 110 : 200, gesture.Selection.Y);
                Equal(gesture.Selection, gesture.FixedSelection(OutputDestination.File)!.Bounds);
            }
        });
        Run("固定Spaceのキーリピートを抑えガイドの基点表示を更新", () =>
        {
            var snapshot = new DesktopSnapshot(new Bitmap(800, 600), new(0, 0, 800, 600), [new(0, 0, 800, 600)]);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.File, FixedSize: new(100, 80)), snapshot, []);
            try
            {
                session.MoveTo(new(400, 300)); session.HandleCommandKey(Keys.Space);
                Equal(CaptureAnchor.TopRight, session.FixedAnchor);
                session.HandleCommandKey(Keys.Space, true); Equal(CaptureAnchor.TopRight, session.FixedAnchor);
                session.HandleCommandKey(Keys.Space | Keys.Shift | Keys.Control); Equal(CaptureAnchor.BottomRight, session.FixedAnchor);
                using var guide = new SelectionGuideForm(new(0, 0, 800, 600), CaptureIntent.Screenshot, () => { }, new(100, 80));
                guide.UpdateAnchor(session.FixedAnchor);
                Check(guide.Controls["SelectionHelp"]!.Text.Contains("基点: 右下"), "ガイドが古い基点");
            }
            finally { session.Cancel(); }
        });
        Run("矩形縦横比は正負4方向とも正確な整数比", () =>
        {
            foreach (var ratio in new[] { CaptureAspectRatio.Wide, CaptureAspectRatio.Standard, CaptureAspectRatio.Square })
                foreach (int sx in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 })
                {
                    var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-1000, -1000, 2000, 2000)], []) { AspectRatio = ratio };
                    gesture.Begin(Point.Empty, new(8, 8)); gesture.Move(new(sx * 201, sy * 81));
                    var size = gesture.Selection.Size;
                    Check(size.Width > 0 && size.Height > 0, "選択できない");
                    Equal(ratio == CaptureAspectRatio.Wide ? 9 : ratio == CaptureAspectRatio.Standard ? 3 : 1,
                        size.Height * (ratio == CaptureAspectRatio.Wide ? 16 : ratio == CaptureAspectRatio.Standard ? 4 : 1) / size.Width);
                    Equal(0, size.Width * (ratio == CaptureAspectRatio.Wide ? 9 : ratio == CaptureAspectRatio.Standard ? 3 : 1)
                        - size.Height * (ratio == CaptureAspectRatio.Wide ? 16 : ratio == CaptureAspectRatio.Standard ? 4 : 1));
                }
        });
        Run("縦横比とグリッド・ロック・Space・画面境界を併用", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 800)], []) { AspectRatio = CaptureAspectRatio.Wide, Grid20 = true };
            gesture.Begin(new(100, 100), new(8, 8)); gesture.Move(new(200, 170));
            int width = gesture.Selection.Width;
            gesture.Move(new(700, 600), lockWidth: true); Equal(width, gesture.Selection.Width);
            gesture.Move(new(999, 799));
            Check(new Rectangle(0, 0, 1000, 800).Contains(gesture.Selection), "画面外へ伸びた");
            Equal(gesture.Selection.Width * 9, gesture.Selection.Height * 16);
            gesture.Space(); gesture.Move(new(500, 500));
            Equal(gesture.Selection.Width * 9, gesture.Selection.Height * 16);
        });
        Run("比率メニューの選択は同セッション内だけで次回自由へ戻る", () =>
        {
            using var menu = new CaptureContextMenu(OutputDestination.File, _ => { }, () => { }, () => { }, changeRatio: _ => { });
            var item = (ToolStripMenuItem)menu.Items["AspectRatioMenu"]!;
            Equal(4, item.DropDownItems.Count);
            menu.SetRatio(CaptureAspectRatio.Wide);
            Check(((ToolStripMenuItem)item.DropDownItems[1]).Checked && !((ToolStripMenuItem)item.DropDownItems[0]).Checked, "比率チェック不一致");
            using var next = new CaptureContextMenu(OutputDestination.File, _ => { }, () => { }, () => { }, changeRatio: _ => { });
            Check(((ToolStripMenuItem)((ToolStripMenuItem)next.Items["AspectRatioMenu"]!).DropDownItems[0]).Checked, "次回へ漏れた");
        });
    }
}
