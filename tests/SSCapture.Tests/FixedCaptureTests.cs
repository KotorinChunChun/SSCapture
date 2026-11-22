using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void FixedCaptureTests()
    {
        Run("両固定枠はSpace一巡とホイール往復を保ちクリックとEnterで表示枠の実画素を取得する", () =>
        {
            Rectangle desktop = new(0, 0, 1200, 800), basis = new(100, 80, 960, 540);
            Rectangle grown = Rectangle.Inflate(basis, 2, 2);
            DisplayLayout display = new("test-fixed", "試験画面", desktop, 96, 96);
            SavedRegion preset = RegionCoordinates.FromBounds("登録枠", basis, [display]);
            var settings = new AppSettings { CaptureBeforeKeys = new() };
            Check(settings.CaptureKeys is { SpaceEnabled: true, WheelAdjustRectangle: true }, "初期設定で固定枠の操作が無効");
            settings = settings with { CaptureKeys = settings.CaptureKeys with { SpaceCycle = CaptureAnchorCycle.CornersAndCenter } };
            Point[] anchors = [new(1060, 80), new(1060, 620), new(100, 620), new(580, 350), new(100, 80)];

            foreach (bool registered in new[] { false, true })
            foreach (bool enter in new[] { false, true })
            {
                var image = new Bitmap(desktop.Width, desktop.Height);
                image.SetPixel(grown.Left, grown.Top, Color.Red);
                image.SetPixel(grown.Right - 1, grown.Bottom - 1, Color.Blue);
                var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    FixedSize: basis.Size, ShowSelectionGuide: false, OutputSettings: settings),
                    new DesktopSnapshot(image, desktop, [desktop]), [], initialPointer: basis.Location, readPresetDisplays: () => [display]);
                using var overlay = new CaptureOverlay(session, desktop);
                try
                {
                    if (registered) session.ApplyInputPresetAsync(preset).GetAwaiter().GetResult();
                    session.MoveTo(basis.Location);
                    Equal(basis, session.DisplaySelection);
                    session.MouseWheel(session.Pointer, 240);
                    Equal(grown, session.DisplaySelection);
                    foreach (Point anchor in anchors)
                    {
                        Check(session.HandleCommandKey(Keys.Space), "固定枠でSpaceを処理しない");
                        Equal(anchor, session.Pointer);
                        session.MoveTo(anchor);
                        Equal(grown, session.DisplaySelection);
                    }
                    session.MouseWheel(session.Pointer, -240);
                    Equal(basis, session.DisplaySelection);
                    Point moved = session.Pointer + new Size(10, 6);
                    session.MoveTo(moved);
                    Equal(registered ? basis : new Rectangle(moved, basis.Size), session.DisplaySelection);
                    session.MoveTo(basis.Location);
                    session.MouseWheel(session.Pointer, 240);
                    Equal(grown, session.DisplaySelection);
                    if (enter) session.HandleCommandKey(Keys.Enter);
                    else
                    {
                        _ = overlay.Handle;
                        session.MouseDown(overlay, MouseButtons.Left, session.Pointer, Keys.None);
                        session.MouseUp(MouseButtons.Left, session.Pointer, Keys.None);
                    }
                    Check(session.Completion.IsCompletedSuccessfully, "調整後の固定枠を確定できない");
                    using var outcome = session.Completion.Result;
                    Equal(grown, outcome.Selection!.Bounds);
                    Equal(grown.Size, outcome.Image!.Size);
                    Equal(Color.Red.ToArgb(), outcome.Image.GetPixel(0, 0).ToArgb());
                    Equal(Color.Blue.ToArgb(), outcome.Image.GetPixel(grown.Width - 1, grown.Height - 1).ToArgb());
                    Equal(basis, RegionCoordinates.Resolve(preset, [display]));
                }
                finally { session.Cancel(); }
            }
        });
        Run("両固定枠の負座標と画面端で対称性を保ち、取消で調整を解除する", () =>
        {
            Rectangle monitor = new(-1200, -800, 1200, 800), basis = new(-960, -540, 960, 540);
            foreach (bool registered in new[] { false, true })
            {
                var gesture = new SelectionGesture(CaptureMode.FixedRegion, [monitor], [], basis.Size);
                if (registered) gesture.FixedPosition = basis;
                gesture.Move(basis.Location);
                gesture.ResizeFixedFrame(240);
                Equal(basis, gesture.Selection);
                gesture.ResizeFixedFrame(-240);
                Rectangle shrunk = Rectangle.Inflate(basis, -2, -2);
                Equal(shrunk, gesture.Selection);
                Check(gesture.Space(point =>
                {
                    Equal(new Point(0, -540), point);
                    return new Point(-1, -540);
                }), "画面端のSpaceで基点を切り替えられない");
                gesture.Move(new(-1, -540));
                Equal(shrunk, gesture.Selection);
                if (!registered)
                {
                    gesture.Move(new(-11, -550));
                    Equal(new Rectangle(shrunk.X - 10, shrunk.Y - 10, shrunk.Width, shrunk.Height), gesture.Selection);
                    gesture.Move(new(-1, -540));
                }
                gesture.Reset();
                Equal(basis, gesture.Selection);
                gesture.ResizeFixedFrame(-120);
                Equal(Rectangle.Inflate(basis, -1, -1), gesture.Selection);
            }
        });
        Run("両固定枠でEsc取り消し後の撮影は登録位置と元の寸法を使う", () =>
        {
            Rectangle desktop = new(0, 0, 500, 400), basis = new(70, 55, 200, 100);
            DisplayLayout display = new("fixed-repeat", "試験画面", desktop, 96, 96);
            SavedRegion preset = RegionCoordinates.FromBounds("登録枠", basis, [display]);
            foreach (bool registered in new[] { false, true })
            foreach (bool cancel in new[] { true, false })
            {
                var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    FixedSize: basis.Size, ShowSelectionGuide: false),
                    new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]), [],
                    initialPointer: basis.Location, readPresetDisplays: () => [display]);
                try
                {
                    if (registered) session.ApplyInputPresetAsync(preset).GetAwaiter().GetResult();
                    session.MoveTo(basis.Location);
                    Equal(basis, session.DisplaySelection);
                    if (cancel)
                    {
                        session.MouseWheel(session.Pointer, 360);
                        Equal(Rectangle.Inflate(basis, 3, 3), session.DisplaySelection);
                        session.HandleCommandKey(Keys.Escape);
                    }
                    else session.HandleCommandKey(Keys.Enter);

                    Check(session.Completion.IsCompletedSuccessfully, "固定枠の終了操作を処理できない");
                    using var outcome = session.Completion.Result;
                    Equal<Rectangle?>(cancel ? null : basis, outcome.Selection?.Bounds);
                    Equal<Size?>(cancel ? null : basis.Size, outcome.Image?.Size);
                }
                finally { session.Cancel(); }
            }
        });
        Run("固定枠で無効としたSpace・ホイールを尊重し、寸法指定の登録は元寸法で確定する", () =>
        {
            Rectangle desktop = new(0, 0, 400, 300), basis = new(20, 30, 100, 80);
            var settings = new AppSettings { CaptureKeys = new(SpaceEnabled: false, WheelAdjustRectangle: false), CaptureBeforeKeys = new() };
            foreach (bool registering in new[] { false, true })
            {
                var options = registering ? CaptureOptions.ForInputSelection(new AppSettings(), basis.Size, CaptureAnchor.TopLeft)
                    : new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                        FixedSize: basis.Size, OutputSettings: settings);
                var session = new SessionController(options,
                    new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, [desktop]), [], initialPointer: basis.Location);
                try
                {
                    session.MoveTo(basis.Location);
                    bool handled = session.HandleCommandKey(Keys.Space);
                    Equal(registering, handled);
                    session.MouseWheel(session.Pointer, 240);
                    Equal(basis, session.Selection);
                }
                finally { session.Cancel(); }
            }
        });
        Run("固定サイズは1pxから辺と総画素数の上限まで指定できる", () =>
        {
            Equal(new Size(1, 1), CaptureGeometry.ValidateFixedSize(new Size(1, 1)));
            Equal(new Size(32768, 1), CaptureGeometry.ValidateFixedSize(new Size(32768, 1)));
            Equal(new Size(10000, 10000), CaptureGeometry.ValidateFixedSize(new Size(10000, 10000)));
            foreach (Size? size in new Size?[] { null, Size.Empty, new(-1, 2), new(32769, 1), new(10001, 10000) })
            {
                bool rejected = false;
                try { CaptureGeometry.ValidateFixedSize(size); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, $"不正寸法を受理した: {size}");
            }
        });
        Run("固定サイズは負座標でも左上基準の物理pxをクリックだけで確定", () =>
        {
            Rectangle monitor = new(-1920, -300, 1920, 1080);
            SelectionGesture gesture = new(CaptureMode.FixedRegion, [monitor], [monitor], new Size(1280, 720));
            Point point = new(-1800, -200);
            gesture.Move(point);
            Equal(new Rectangle(point, new Size(1280, 720)), gesture.Selection);
            gesture.Begin(point, new Size(32, 32));
            CaptureSelection result = gesture.End(point, OutputDestination.Clipboard | OutputDestination.File)!;
            Equal(new Rectangle(-1800, -200, 1280, 720), result.Bounds);
            Equal(OutputDestination.Clipboard | OutputDestination.File, result.Destination);
            Check(result.Polygon is null, "固定サイズは矩形");
            gesture.Begin(point, new Size(8, 8));
            Equal(new Size(1280, 720), gesture.End(new Point(-1700, -100), OutputDestination.File)!.Bounds.Size);
        });
        Run("固定サイズの矢印は1px、Shift矢印は10pxだけ移動", () =>
        {
            SelectionGesture gesture = new(CaptureMode.FixedRegion, [new(-1920, -300, 3840, 2160)], [], new Size(640, 480));
            gesture.Move(new Point(-500, 50));
            Check(gesture.Nudge(Keys.Right), "右キーを処理");
            Check(gesture.Nudge(Keys.Up | Keys.Shift), "Shift上キーを処理");
            Check(gesture.Nudge(Keys.Left), "左キーを処理");
            Check(gesture.Nudge(Keys.Down | Keys.Shift), "Shift下キーを処理");
            Equal(new Rectangle(-500, 50, 640, 480), gesture.Selection);
            Check(!gesture.Nudge(Keys.Control | Keys.Right), "Ctrlキーを矢印操作に変換しない");
            Check(!gesture.Nudge(Keys.A), "文字入力を処理しない");
            gesture.Move(new Point(int.MaxValue - 640, int.MinValue));
            gesture.Nudge(Keys.Right | Keys.Shift);
            gesture.Nudge(Keys.Up | Keys.Shift);
            Equal(new Point(int.MaxValue - 640, int.MinValue), gesture.Current);
        });
        Run("固定サイズはモニタ間隙と外側を拒否し連続する複数画面を許容", () =>
        {
            Rectangle left = new(-100, 0, 100, 100), right = new(0, 0, 100, 100);
            Check(CaptureGeometry.IsFullyCovered(new(-10, 20, 20, 30), [left, right]), "連続画面を横断");
            Check(CaptureGeometry.IsFullyCovered(new(-100, 0, 200, 100), [left, right, left]), "重複画面を二重計上しない");
            Check(!CaptureGeometry.IsFullyCovered(new(-10, 20, 30, 30), [left, new(10, 0, 100, 100)]), "画面間の隙間を拒否");
            Check(!CaptureGeometry.IsFullyCovered(new(-10, 20, 20, 30), [left, new(0, 40, 100, 100)]), "上下にずれた画面の欠けを拒否");
            Check(!CaptureGeometry.IsFullyCovered(new(-101, 20, 30, 30), [left, right]), "左端の1pxはみ出しを拒否");
            Check(!CaptureGeometry.IsFullyCovered(new(int.MaxValue, 0, 2, 2), [left]), "座標加算のoverflowを拒否");
            SelectionGesture gesture = new(CaptureMode.FixedRegion, [left], [left], new Size(20, 30));
            gesture.Move(new Point(-10, 20));
            Check(gesture.FixedSelection(OutputDestination.File) is null, "外側は確定しない");
            gesture.Move(new Point(-20, 20));
            Equal(new Rectangle(-20, 20, 20, 30), gesture.FixedSelection(OutputDestination.File)!.Bounds);
            Equal(left, gesture.MonitorSelection(new Point(-20, 20), OutputDestination.File)!.Bounds);
        });
        Run("固定サイズの1px撮影は元画像の1pxをそのまま返す", () =>
        {
            Rectangle desktop = new(-20, -10, 12, 12);
            Bitmap image = PatternBitmap();
            int expected = image.GetPixel(2, 3).ToArgb();
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                FixedSize: new Size(1, 1)), new DesktopSnapshot(image, desktop, [desktop]), []);
            session.MoveTo(new Point(-18, -7));
            Check(session.HandleCommandKey(Keys.Enter), "Enterで確定");
            using CaptureOutcome outcome = session.Completion.GetAwaiter().GetResult();
            Equal(new Rectangle(-18, -7, 1, 1), outcome.Selection!.Bounds);
            Equal(new Size(1, 1), outcome.Image!.Size);
            Equal(expected, outcome.Image.GetPixel(0, 0).ToArgb());
        });
        Run("固定位置のキー調整を同じ物理カーソル位置の通知で戻さない", () =>
        {
            Rectangle desktop = new(-20, -10, 12, 12);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                FixedSize: new Size(2, 2)), new DesktopSnapshot(PatternBitmap(), desktop, [desktop]), []);
            try
            {
                session.MoveTo(new Point(-18, -7));
                session.HandleCommandKey(Keys.Right);
                session.MoveTo(new Point(-18, -7));
                Equal(new Rectangle(-17, -7, 2, 2), session.Selection);
                session.MoveTo(new Point(-16, -5));
                Equal(new Rectangle(-16, -5, 2, 2), session.Selection);
            }
            finally { session.Cancel(); }
        });
        Run("固定範囲外のEnterは選択を継続しEscapeで終了", () =>
        {
            Rectangle desktop = new(0, 0, 12, 12);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.File,
                FixedSize: new Size(4, 4)), new DesktopSnapshot(PatternBitmap(), desktop, [desktop]), []);
            session.MoveTo(new Point(10, 10));
            Check(!session.FixedSelectionValid, "不正枠を色表示で区別");
            Check(session.HandleCommandKey(Keys.Enter), "範囲外Enterを処理");
            Check(!session.Completion.IsCompleted, "範囲外を保存やキャンセルとして終了しない");
            session.CompleteSelection(new(new Rectangle(0, 0, 5, 5), null, OutputDestination.File));
            Check(!session.Completion.IsCompleted, "固定寸法以外の結果も拒否");
            Check(session.HandleCommandKey(Keys.Escape), "Escapeでキャンセル");
            using CaptureOutcome result = session.Completion.GetAwaiter().GetResult();
            Check(result.Selection is null && result.Image is null, "キャンセル結果に画像なし");
        });
        Run("固定範囲でも出力先メニューの複数選択を維持", () =>
        {
            Rectangle desktop = new(0, 0, 12, 12);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                FixedSize: new Size(2, 2)), new DesktopSnapshot(PatternBitmap(), desktop, [desktop]), []);
            session.SelectionMenu.AllCommands.Single(item => Equals(item.Tag, OutputDestination.File)).PerformClick();
            session.MoveTo(new Point(3, 3));
            session.HandleCommandKey(Keys.Enter);
            using CaptureOutcome result = session.Completion.GetAwaiter().GetResult();
            Equal(OutputDestination.Clipboard | OutputDestination.File, result.Selection!.Destination);
        });
        Run("固定撮影の画面固定OFFも選択時点の画像を正確な寸法で切り出す", () =>
        {
            Rectangle desktop = new(0, 0, 12, 12);
            DesktopSnapshot Future()
            {
                Bitmap future = PatternBitmap();
                future.SetPixel(3, 3, Color.Blue);
                return new(future, desktop, [desktop]);
            }
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                FreezeFrame: false, IncludeCursor: true, FixedSize: new Size(2, 2)),
                new DesktopSnapshot(PatternBitmap(), desktop, [desktop]), [], Future);
            session.MoveTo(new Point(3, 3));
            session.HandleCommandKey(Keys.Enter);
            using CaptureOutcome result = session.Completion.GetAwaiter().GetResult();
            Equal(new Size(2, 2), result.Image!.Size);
            Equal(Color.Blue.ToArgb(), result.Image.GetPixel(0, 0).ToArgb());
        });
        Run("固定サイズガイドは寸法とキー説明の領域を確保", () =>
        {
            using var guide = new SelectionGuideForm(new Rectangle(0, 0, 1920, 1080), CaptureIntent.Screenshot, () => { }, new Size(1280, 720));
            guide.CreateControl();
            LayoutTree(guide);
            var help = (Label)guide.Controls.Find("SelectionHelp", true).Single();
            Check(help.Width >= 590 && help.Height >= 85, "固定サイズガイドの説明領域不足");
            Check(guide.ClientRectangle.Contains(help.Bounds), "ガイドが領域外");
            Check(help.Text.Contains("基準 1280 × 720", StringComparison.Ordinal) && help.Text.Contains("Shift", StringComparison.Ordinal)
                && help.Text.Contains("ホイール", StringComparison.Ordinal), "固定操作説明を表示");
            using var registeredGuide = new SelectionGuideForm(new(0, 0, 1920, 1080), CaptureIntent.Screenshot, () => { }, new(960, 540),
                spaceEnabled: false, registeredFrame: true, fixedWheelEnabled: false);
            string registeredHelp = registeredGuide.Controls["SelectionHelp"]!.Text;
            Check(registeredHelp.Contains("固定位置とサイズ") && registeredHelp.Contains("Enter")
                && !registeredHelp.Contains("矢印") && !registeredHelp.Contains("Space") && !registeredHelp.Contains("ホイール"),
                "登録枠で無効な操作を案内した");
            Size measured = TextRenderer.MeasureText(help.Text, help.Font,
                new Size(help.Width - help.Padding.Horizontal, int.MaxValue), TextFormatFlags.WordBreak);
            Check(measured.Height <= help.Height - help.Padding.Vertical, $"説明が切れる: {measured.Height} > {help.Height}");
            help.Dock = DockStyle.None;
            help.Size = new Size(100, 23);
            LayoutTree(guide);
            Check(help.Width < 590 || help.Height < 85, "Dock欠落を検出可能");
        });
    }
}
