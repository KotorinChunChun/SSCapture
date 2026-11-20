using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionUpgradeTests()
    {
        Run("修飾キー10組を個別に選び、キー設定とSpaceの無効を保存できる", () => WithServiceDirectory(directory =>
        {
            var map = new CaptureKeyMap(Ctrl: CaptureKeyAction.Snap, CtrlAlt: CaptureKeyAction.LockHeight, SpaceEnabled: false);
            using var editor = new CaptureKeyEditor(map);
            Equal(10, CaptureKeyMap.Combinations.Distinct().Count());
            Equal(map, editor.Value);
            ((ComboBox)editor.Controls.Find("CaptureKeys5", true).Single()).SelectedIndex = (int)CaptureKeyAction.CrossMonitor;
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings { CaptureKeys = editor.Value });
            Equal(map with { CtrlAlt = CaptureKeyAction.CrossMonitor }, store.Load(out _).CaptureKeys);
            Equal(CaptureKeyAction.None, map.Resolve(CaptureModifiers.Ctrl | CaptureModifiers.Alt | CaptureModifiers.Shift));
        }));
        Run("ウィンドウ強調はWin単押しで切り替え最前面の撮影対象を追う", () =>
        {
            Rectangle monitor = new(0, 0, 600, 400);
            Rectangle front = new(100, 100, 140, 160), back = new(10, 10, 300, 300);
            var snapshot = new DesktopSnapshot(new Bitmap(600, 400), monitor, [monitor]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                LimitToStartingMonitor: false), snapshot, [front, back]);
            try
            {
                session.ApplyPointerInput(new(110, 110), CaptureModifiers.Win);
                Check(!session.HighlightWindow, "単押しが確定する前に切り替わった");
                session.ApplyPointerInput(new(110, 110), CaptureModifiers.None, windowsTap: true);
                Equal(front, session.HighlightedWindow);
                session.ApplyPointerInput(new(20, 20), CaptureModifiers.Ctrl);
                Equal("Ctrl : 上下に変更", session.ModifierHint);
                Equal(back, session.HighlightedWindow);
                session.ApplyPointerInput(new(20, 20), CaptureModifiers.None, windowsTap: true);
                Equal(Rectangle.Empty, session.HighlightedWindow);
                Check(session.ModifierHint is null, "修飾キーを離しても効果が表示される");
            }
            finally { session.Cancel(); }
        });
        Run("左単押しの対象名を撮影結果に添え、矩形ドラッグには添えない", () =>
        {
            Rectangle monitor = new(0, 0, 600, 400);
            Rectangle window = new(40, 40, 240, 180);
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [monitor], [window]);
            Point pointer = new(110, 110);
            gesture.Begin(pointer, new(8, 8));
            var selected = gesture.End(pointer, OutputDestination.Clipboard)!;
            var snapshot = new DesktopSnapshot(new Bitmap(600, 400), monitor, [monitor]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                snapshot, [window], readTitle: point => point == pointer ? "検証用ウィンドウ" : "");
            session.CompleteSelection(selected);
            using var outcome = session.Completion.GetAwaiter().GetResult();
            Equal("検証用ウィンドウ", outcome.Selection!.WindowTitle);
            gesture.Begin(pointer, new(8, 8));
            Check(gesture.End(new(150, 150), OutputDestination.Clipboard)!.WindowHitPoint is null, "任意の矩形にウィンドウ名を付けた");
        });
        Run("矩形Spaceは選択を保ちポインタを対角へ移し逆側を操作する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], []);
            gesture.Begin(new(100, 100), new(8, 8)); gesture.Move(new(200, 180));
            gesture.Move(new(400, 350), lockHeight: true, lockWidth: true);
            Check(gesture.Space(target => { Equal(new Point(100, 100), target); return target; }), "Spaceを処理しない");
            Check(gesture.IsPressed && gesture.HasDragged, "ドラッグを終了した");
            Equal(new Rectangle(100, 100, 100, 80), gesture.Selection);
            gesture.Move(new(90, 85));
            Equal(new Rectangle(90, 85, 110, 95), gesture.Selection);
            Equal(gesture.Selection, gesture.End(new(90, 85), OutputDestination.File)!.Bounds);
        });
        Run("Space直後に離すと元の矩形を確定する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], [new(0, 0, 900, 900)]);
            Check(!gesture.Space(), "未ドラッグのSpaceを処理した");
            gesture.Begin(new(10, 10), new(8, 8)); gesture.Move(new(100, 100)); gesture.Space();
            Equal(new Rectangle(10, 10, 90, 90), gesture.End(new(10, 10), OutputDestination.Clipboard)!.Bounds);
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
            var item = (ToolStripMenuItem)menu.Command("AspectRatioMenu")!;
            Equal(4, item.DropDownItems.Count);
            menu.SetRatio(CaptureAspectRatio.Wide);
            Check(((ToolStripMenuItem)item.DropDownItems[1]).Checked && !((ToolStripMenuItem)item.DropDownItems[0]).Checked, "比率チェック不一致");
            using var next = new CaptureContextMenu(OutputDestination.File, _ => { }, () => { }, () => { }, changeRatio: _ => { });
            Check(((ToolStripMenuItem)((ToolStripMenuItem)next.Command("AspectRatioMenu")!).DropDownItems[0]).Checked, "次回へ漏れた");
        });
    }
}
