using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionInteractionTests()
    {
        static SessionController RectangleSelectionSession(Services.AppSettings settings) =>
            OutputSession(settings with { CaptureKeys = new(), CaptureBeforeKeys = new() });

        Run("ポインタ横のガイドは左単押しのウィンドウ名を負座標のドラッグ範囲に切り替える", () =>
        {
            Rectangle monitor = new(-400, -100, 400, 300);
            Rectangle windowBounds = new(-340, -80, 180, 120);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var window = new CaptureWindow(windowBounds, "テストウィンドウ", 1, (uint)process.Id, 1);
            var snapshot = new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None),
                snapshot, [windowBounds], initialPointer: new(-300, -50), findWindow: point => windowBounds.Contains(point) ? window : null);
            using var overlay = new CaptureOverlay(session, monitor);
            try
            {
                _ = overlay.Handle;
                Check(session.PointerHint is null, "未選択のウィンドウ名が表示された");
                session.MouseDown(overlay, MouseButtons.Left, new(-300, -50));
                Equal("ウィンドウを撮影　Esc / 右クリックで取り消し\nテストウィンドウ（" + process.ProcessName + "）\n座標（-340, -80　180 × 120）　余白0px", session.PointerHint);
                session.MoveTo(new(-211, 27));
                Equal("選択範囲を撮影　Esc / 右クリックで取り消し\n座標（-300, -50　89 × 77）", session.PointerHint);
                session.AbortSelection();
                Check(session.PointerHint is null, "取り消した対象が表示された");
            }
            finally { session.Cancel(); }
        });
        Run("ポインタ横のガイドは中ドラッグで通過したモニターと名称を一度ずつ表示する", () =>
        {
            Rectangle left = new(-160, 0, 160, 120), right = new(0, 0, 160, 120);
            var snapshot = new DesktopSnapshot(new Bitmap(320, 120), Rectangle.Union(left, right), [left, right]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None),
                snapshot, [], initialPointer: new(-95, 30),
                readGuideDisplays: () => [new("DISPLAY2", right, true, "メイン画面"), new("DISPLAY1", left, false, "左の画面")]);
            using var overlay = new CaptureOverlay(session, left);
            try
            {
                _ = overlay.Handle;
                session.MouseDown(overlay, MouseButtons.Middle, new(-95, 30));
                Check(session.PointerHint?.EndsWith("\nデスクトップ 2（左の画面）", StringComparison.Ordinal) == true, "1台目のモニターが表示されない");
                session.MoveTo(new(50, 30));
                session.MoveTo(new(-95, 30));
                Equal("モニター全体を撮影　Esc / 右クリックで取り消し\nドラッグ：複数デスクトップを合成\nデスクトップ 2（左の画面）\nデスクトップ 1（メイン画面）", session.PointerHint);
            }
            finally { session.Cancel(); }
        });
        Run("ポインタ横のガイドはウィンドウドラッグで前面の子ウィンドウと通過した対象を表示する", () =>
        {
            Rectangle monitor = new(0, 0, 400, 300);
            var first = new CaptureWindow(new(20, 20, 100, 100), "最初", 1, 0, 1);
            var child = new CaptureWindow(new(60, 75, 120, 80), "子ウィンドウ", 2, 0, 1);
            var last = new CaptureWindow(new(200, 20, 140, 100), "最後", 3, 0, 1);
            CaptureWindow[] targets = [child, first, last];
            var snapshot = new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]);
            var session = new SessionController(new(CaptureMode.Window, CaptureIntent.Screenshot, OutputDestination.None),
                snapshot, targets.Select(target => target.Bounds).ToArray(), initialPointer: new(30, 40),
                findWindow: point => targets.FirstOrDefault(target => target.Bounds.Contains(point)),
                findWindowGroup: root => root == first ? [child, first] : [root]);
            using var overlay = new CaptureOverlay(session, monitor);
            try
            {
                _ = overlay.Handle;
                session.MouseDown(overlay, MouseButtons.Left, new(30, 40));
                session.MoveTo(new(220, 40));
                session.MoveTo(new(30, 40));
                Equal("ウィンドウを撮影　Esc / 右クリックで取り消し\nドラッグ：複数ウィンドウを合成\n最初（PID 0）\n子ウィンドウ（PID 0）\n最後（PID 0）", session.PointerHint);
            }
            finally { session.Cancel(); }
        });
        Run("固定サイズの座標はガイドONの場合だけ表示する", () =>
        {
            Rectangle monitor = new(0, 0, 400, 300);
            foreach (bool show in new[] { true, false })
            {
                var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.None,
                    FixedSize: new(100, 80), OutputSettings: new() { ShowPointerGuide = show }),
                    new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]), [], initialPointer: new(30, 40));
                try
                {
                    session.MoveTo(new(30, 40));
                    Equal(show ? "座標（30, 40　100 × 80）" : null, session.PointerHint);
                }
                finally { session.Cancel(); }
            }
        });
        Run("矩形の2点クリックは1点目で確定せず終点まで同じ始点を使う", () =>
        {
            var session = RectangleSelectionSession(new Services.AppSettings { SelectionMethod = CaptureSelectionMethod.TwoClicks });
            using var overlay = new CaptureOverlay(session, new(0, 0, 400, 300));
            try
            {
                _ = overlay.Handle;
                Check(session.MagnifierMarksOrigin, "起点を選ぶ前に十字線がない");
                Click(new(240, 180));
                Check(!session.Completion.IsCompleted && session.Dragging, "1点目で選択を終えた");
                Check(session.MagnifierMarksOrigin, "起点を決めた直後に十字線が消えた");
                Equal(new Point(240, 180), session.MagnifierSample);
                Click(new(240, 180));
                Check(!session.Completion.IsCompleted, "ゼロ寸法を確定した");
                session.MoveTo(new(40, 50));
                Equal(new Rectangle(40, 50, 200, 130), session.DisplaySelection);
                Check(!session.MagnifierMarksOrigin, "終点でも起点用の十字線が表示された");
                Equal(new Point(40, 50), session.MagnifierSample);
                Click(new(40, 50));
                using var result = session.Completion.GetAwaiter().GetResult();
                Equal(new Rectangle(40, 50, 200, 130), result.Selection!.Bounds);
                Equal(new Size(200, 130), result.Image!.Size);
            }
            finally { session.Cancel(); }
            void Click(Point point)
            {
                session.MouseDown(overlay, MouseButtons.Left, point);
                session.MouseUp(MouseButtons.Left, point);
            }
        });
        Run("2点目の待機をEscで取り消すと次の左クリックが新しい始点になる", () =>
        {
            var session = RectangleSelectionSession(new Services.AppSettings { SelectionMethod = CaptureSelectionMethod.TwoClicks });
            using var overlay = new CaptureOverlay(session, new(0, 0, 400, 300));
            try
            {
                _ = overlay.Handle;
                session.MouseDown(overlay, MouseButtons.Left, new(40, 50));
                session.MouseUp(MouseButtons.Left, new(40, 50));
                Check(session.HandleCommandKey(Keys.Escape), "Escが処理されなかった");
                Check(!session.Dragging && !session.Completion.IsCompleted, "始点を取り消せなかった");
                session.MouseDown(overlay, MouseButtons.Left, new(100, 100));
                session.MouseUp(MouseButtons.Left, new(100, 100));
                session.MoveTo(new(160, 140));
                Equal(new Rectangle(100, 100, 60, 40), session.DisplaySelection);
            }
            finally { session.Cancel(); }
        });
        Run("矢印の1ドット調整はグリッドを一時的に使わず元の始点を維持する", () =>
        {
            var session = RectangleSelectionSession(new Services.AppSettings { SelectionMethod = CaptureSelectionMethod.TwoClicks, GridStepPixels = 20 });
            using var overlay = new CaptureOverlay(session, new(0, 0, 400, 300));
            try
            {
                _ = overlay.Handle;
                session.ChangeGrid20(true);
                session.MouseDown(overlay, MouseButtons.Left, new(40, 50));
                session.MouseUp(MouseButtons.Left, new(40, 50));
                session.MoveTo(new(90, 100));
                Check(session.HandleCommandKey(Keys.Right), "矢印が処理されなかった");
                Equal(new Point(91, 100), session.Pointer);
                Equal(new Rectangle(40, 50, 51, 50), session.DisplaySelection);
            }
            finally { session.Cancel(); }
        });
        Run("高速ドラッグのDown/Upは現在カーソル位置ではなくイベント座標で確定", () =>
        {
            var session = RectangleSelectionSession(new Services.AppSettings());
            using var overlay = new CaptureOverlay(session, new(0, 0, 400, 300));
            try
            {
                _ = overlay.Handle;
                session.MouseDown(overlay, MouseButtons.Left, new(40, 50));
                session.MouseUp(MouseButtons.Left, new(240, 180));
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(new Rectangle(40, 50, 200, 130), outcome.Selection!.Bounds);
            }
            finally { session.Cancel(); }
        });
        Run("Shift高さ固定を離すと静止中でもポインタの角へ復元", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(100, 100), new(8, 8));
            g.Move(new(180, 160));
            g.Move(new(180, 160), lockHeight: true);
            g.Move(new(220, 260), lockHeight: true);
            Equal(new Rectangle(100, 100, 120, 60), g.Selection);
            g.Move(new(220, 260));
            Equal(new Point(220, 260), g.Current);
            Equal(new Rectangle(100, 100, 120, 160), g.Selection);
            g.Move(new(230, 270));
            Equal(new Rectangle(100, 100, 130, 170), g.Selection);
        });
        Run("Ctrl幅固定・両キー・逆方向・繰り返し固定", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(400, 400), new(8, 8)); g.Move(new(300, 320));
            g.Move(new(200, 280), lockWidth: true);
            Equal(new Rectangle(300, 280, 100, 120), g.Selection);
            g.Move(new(100, 100), lockHeight: true, lockWidth: true);
            Equal(new Rectangle(300, 280, 100, 120), g.Selection);
            g.Move(new(100, 100));
            Equal(new Point(100, 100), g.Current);
            Equal(new Rectangle(100, 100, 300, 300), g.Selection);
            g.Move(new(80, 70));
            Equal(new Rectangle(80, 70, 320, 330), g.Selection);
            g.Move(new(60, 20), lockWidth: true);
            Equal(new Rectangle(80, 20, 320, 380), g.Selection);
            Equal(new Rectangle(60, 20, 340, 380), g.End(new(60, 20), OutputDestination.Clipboard)!.Bounds);
        });
        Run("方向ロックは片軸だけの解除と再固定を独立して処理", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(300, 300), new(8, 8)); g.Move(new(500, 450));
            g.Move(new(650, 700), lockHeight: true, lockWidth: true);
            Equal(new Point(500, 450), g.Current);
            g.Move(new(650, 700), lockWidth: true);
            Equal(new Point(500, 700), g.Current);
            g.Move(new(600, 600), lockHeight: true);
            Equal(new Point(600, 700), g.Current);
            g.Move(new(600, 600));
            Equal(new Point(600, 600), g.Current);
            g.Move(new(50, 60), lockWidth: true);
            Equal(new Point(600, 60), g.Current);
            g.Move(new(50, 60));
            Equal(new Point(50, 60), g.Current);
            Equal(new Rectangle(50, 60, 250, 240), g.End(new(50, 60), OutputDestination.File)!.Bounds);
        });
        Run("ドラッグ前の寸法固定でゼロ寸法に閉じ込めない", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(100, 100), new(8, 8), lockHeight: true);
            g.Move(new(150, 160), lockHeight: true);
            Equal(new Size(50, 60), g.Selection.Size);
            g.Move(new(180, 200), lockHeight: true);
            Equal(new Size(80, 60), g.Selection.Size);
        });
        Run("グリッドは開始点基準で一時使用でき、枠スナップと独立する", () =>
        {
            var g = RectangleGesture();
            g.SetSnapPrecision(20, 20);
            g.Begin(new(103, 107), new(8, 8)); g.Move(new(176, 162));
            Equal(new Size(73, 55), g.Selection.Size);
            g.Move(new(176, 162), true);
            Equal(new Size(73, 55), g.Selection.Size);
            g.Move(new(176, 162), action: CaptureKeyAction.Grid);
            Equal(new Size(80, 60), g.Selection.Size);
            g.Move(new(176, 162)); Equal(new Size(73, 55), g.Selection.Size);
            g.Grid20 = true; g.Move(new(46, 34)); Equal(new Rectangle(43, 27, 60, 80), g.Selection);
            g.Move(new(999, 999)); Equal(new Size(897, 893), g.Selection.Size); // モニタ端1000をグリッドより優先。
        });
        Run("枠スナップはグリッドより優先し固定寸法を維持", () =>
        {
            var g = RectangleGesture();
            g.SetSnapPrecision(20, 20);
            g.Begin(new(103, 107), new(8, 8)); g.Move(new(183, 167));
            g.Move(new(990, 990), true, lockHeight: true, action: CaptureKeyAction.Grid);
            Equal(new Rectangle(103, 107, 897, 60), g.Selection);
        });
        Run("寸法固定解除は画面内の実ポインタ位置へ戻り境界を越えない", () =>
        {
            var g = RectangleGesture(); g.Begin(new(100, 100), new(8, 8)); g.Move(new(800, 800));
            g.Move(new(200, 200), lockHeight: true, lockWidth: true);
            g.Move(new(200, 200)); g.Move(new(999, 999));
            Equal(new Point(999, 999), g.Current);
            Equal(new Rectangle(100, 100, 899, 899), g.Selection);
        });
        Run("丸めと修飾キーだけでは単押しをドラッグにしない", () =>
        {
            var g = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], [new(90, 90, 200, 150)]) { Grid20 = true };
            g.Begin(new(103, 107), new(8, 8));
            g.Move(new(103, 107), true, true, true);
            Check(!g.HasDragged, "静止切替をドラッグと誤認");
            Equal(new Rectangle(90, 90, 200, 150), g.End(new(104, 108), OutputDestination.File)!.Bounds);
        });
        Run("選択Resetは寸法固定と途中の点を捨てて再選択できる", () =>
        {
            var g = RectangleGesture(); g.Begin(new(100, 100), new(8, 8)); g.Move(new(200, 200), lockHeight: true);
            g.Reset(); Check(!g.IsPressed && g.Points.Count == 0 && g.Selection.IsEmpty, "中断状態");
            g.Begin(new(300, 300), new(8, 8));
            Equal(new Rectangle(300, 300, 80, 90), g.End(new(380, 390), OutputDestination.File)!.Bounds);
        });
        Run("20px設定は旧設定でOFF・保存往復で保持", () => WithServiceDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new Services.SettingsStore(path);
            File.WriteAllText(path, "{}");
            Check(!store.Load(out _).Grid20, "旧設定でON");
            foreach (bool enabled in new[] { true, false })
            {
                store.Save(new Services.AppSettings { Grid20 = enabled });
                Equal(enabled, store.Load(out _).Grid20);
            }
        }));
        Run("一時モードと20pxメニューの状態・呼出し・全画面確定", () =>
        {
            var session = MonitorSession(true);
            try
            {
                var modeMenu = (ToolStripMenuItem)session.SelectionMenu.Command("CaptureModeMenu")!;
                Equal(5, modeMenu.DropDownItems.Count);
                ((ToolStripMenuItem)modeMenu.DropDownItems.Cast<ToolStripMenuItem>().Single(i => (CaptureMode)i.Tag! == CaptureMode.Window)).PerformClick();
                Equal(CaptureMode.Window, session.Mode);
                session.MoveTo(new(-100, 20)); Equal(SelectionTestMonitors[0], session.Selection);
                var grid = (ToolStripMenuItem)session.SelectionMenu.Command("Grid20")!;
                grid.PerformClick(); Check(session.Grid20 && grid.Checked, "20pxが有効にならない");
                session.ChangeMode(CaptureMode.FixedRegion);
                Check(session.Grid20, "モード変更で20px設定が失われた");
                Equal(new Size(100, 100), session.Selection.Size);
                session.ChangeMode(CaptureMode.FullScreen);
                using var result = session.Completion.GetAwaiter().GetResult();
                Equal(SelectionTestMonitors[0], result.Selection!.Bounds);
            }
            finally { session.Cancel(); }
        });
        Run("固定サイズから対象モニタ全体を明示確定・限定OFFの位置", () =>
        {
            foreach (bool limited in new[] { false, true })
            {
                var session = MonitorSession(limited, CaptureMode.FixedRegion);
                try
                {
                    session.CaptureDesktop(limited ? new(-100, 20) : new(200, 100));
                    using var result = session.Completion.GetAwaiter().GetResult();
                    Equal(SelectionTestMonitors[limited ? 0 : 1], result.Selection!.Bounds);
                }
                finally { session.Cancel(); }
            }
        });
        Run("画面固定は選択中断・一時モード後もON開始画像/OFF確定後画像", () =>
        {
            foreach (bool freeze in new[] { true, false })
            {
                Rectangle desktop = new(0, 0, 100, 100);
                DesktopSnapshot Frame(Color color)
                {
                    Bitmap bitmap = new(100, 100);
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
                    return new(bitmap, desktop, [desktop]);
                }
                int reads = 0;
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard, FreezeFrame: freeze),
                    Frame(Color.Red), [desktop], () => { reads++; return Frame(Color.Blue); }, new(20, 20));
                try
                {
                    session.AbortSelection(); session.ChangeMode(CaptureMode.Window);
                    session.CompleteSelection(new(new(10, 10, 30, 30), null, OutputDestination.Clipboard));
                    using var result = session.Completion.GetAwaiter().GetResult();
                    Equal(freeze ? 0 : 1, reads);
                    Equal((freeze ? Color.Red : Color.Blue).ToArgb(), result.Image!.GetPixel(15, 15).ToArgb());
                    Equal(OutputDestination.Clipboard, result.Selection!.Destination);
                }
                finally { session.Cancel(); }
            }
        });
        Run("全設定の入力・ボタン・行ラベルにツールチップを付ける", () =>
        {
            using var form = new UI.SettingsForm(new Services.AppSettings(), _ => null, _ => { }, () => []);
            var tips = (ToolTip)typeof(UI.SettingsForm).GetField("settingTips", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form)!;
            foreach (Control control in Descendants(form).Where(c => c is TextBox or ComboBox or CheckBox or NumericUpDown or Button))
            {
                if (control.Name == "Status") continue;
                string description = tips.GetToolTip(control) ?? "";
                Check(!string.IsNullOrWhiteSpace(description), "説明欠落: " + control.Name);
                Check(!description.Contains("ONでは") && !description.Contains("OFFでは"), "表記不一致: " + control.Name);
            }
            var freeze = form.Controls.Find("FreezeFrame", true).Single();
            var table = (TableLayoutPanel)freeze.Parent!;
            var label = (Label)table.GetControlFromPosition(0, table.GetRow(freeze))!;
            Equal("撮影タイミング", label.Text);
            Equal(tips.GetToolTip(freeze), tips.GetToolTip(label));
            _ = form.Handle; foreach (Control child in Descendants(form)) _ = child.Handle; LayoutTree(form);
            Check(label.Parent!.ClientRectangle.Contains(label.Bounds) && label.PreferredSize.Width <= label.Width,
                $"撮影タイミングが見切れる: label={label.Bounds}, preferred={label.PreferredSize}, parent={label.Parent.ClientRectangle}");
        });
    }

    private static SelectionGesture RectangleGesture() => new(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], []);
}
