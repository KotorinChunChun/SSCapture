using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionInteractionTests()
    {
        Run("高速ドラッグのDown/Upは現在カーソル位置ではなくイベント座標で確定", () =>
        {
            var session = OutputSession(new Services.AppSettings());
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
        Run("20px丸めは開始点基準・負方向・Alt一時有効・境界", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(103, 107), new(8, 8)); g.Move(new(176, 162));
            Equal(new Size(73, 55), g.Selection.Size);
            g.Move(new(176, 162), true);
            Equal(new Size(80, 60), g.Selection.Size);
            g.Move(new(176, 162)); Equal(new Size(73, 55), g.Selection.Size);
            g.Grid20 = true; g.Move(new(46, 34)); Equal(new Rectangle(43, 27, 60, 80), g.Selection);
            g.Move(new(999, 999)); Equal(new Size(880, 880), g.Selection.Size);
        });
        Run("Alt枠吸着は20px丸めより優先し固定寸法を維持", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(103, 107), new(8, 8)); g.Move(new(183, 167));
            g.Move(new(990, 990), true, lockHeight: true);
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
                var modeMenu = (ToolStripMenuItem)session.SelectionMenu.Items["CaptureModeMenu"]!;
                Equal(5, modeMenu.DropDownItems.Count);
                ((ToolStripMenuItem)modeMenu.DropDownItems.Cast<ToolStripMenuItem>().Single(i => (CaptureMode)i.Tag! == CaptureMode.Window)).PerformClick();
                Equal(CaptureMode.Window, session.Mode);
                session.MoveTo(new(-100, 20)); Equal(SelectionTestMonitors[0], session.Selection);
                var grid = (ToolStripMenuItem)session.SelectionMenu.Items["Grid20"]!;
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
        Run("モード子メニューは内部フォーカス遷移で閉じず項目確定と外部切替で閉じる", () =>
        {
            bool internalFocus = true;
            using var menu = new CaptureContextMenu(OutputDestination.Clipboard, _ => { }, () => { }, () => { },
                changeMode: _ => { }, ownsPopupFocus: () => internalFocus);
            var child = ((ToolStripMenuItem)menu.Items["CaptureModeMenu"]!).DropDown;
            bool Closing(ToolStripDropDownCloseReason reason)
            {
                var args = new ToolStripDropDownClosingEventArgs(reason);
                child.GetType().GetMethod("OnClosing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(child, [args]);
                return args.Cancel;
            }
            Check(Closing(ToolStripDropDownCloseReason.AppFocusChange), "内部切替で閉じた");
            Check(!Closing(ToolStripDropDownCloseReason.ItemClicked), "モード選択で閉じない");
            internalFocus = false;
            Check(!Closing(ToolStripDropDownCloseReason.AppFocusChange), "他アプリ切替で閉じない");
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
            using var form = new UI.SettingsForm(new Services.AppSettings(), _ => null, () => { }, () => []);
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
