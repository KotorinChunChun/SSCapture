using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionInteractionTests()
    {
        Run("Shift高さ固定と解除後の連続移動", () =>
        {
            var g = RectangleGesture();
            g.Begin(new(100, 100), new(8, 8));
            g.Move(new(180, 160));
            g.Move(new(180, 160), lockHeight: true);
            g.Move(new(220, 260), lockHeight: true);
            Equal(new Rectangle(100, 100, 120, 60), g.Selection);
            g.Move(new(220, 260));
            Equal(new Rectangle(100, 100, 120, 60), g.Selection);
            g.Move(new(230, 270));
            Equal(new Rectangle(100, 100, 130, 70), g.Selection);
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
            Equal(new Rectangle(300, 280, 100, 120), g.Selection);
            g.Move(new(80, 70));
            Equal(new Rectangle(280, 250, 120, 150), g.Selection);
            g.Move(new(60, 20), lockWidth: true);
            Equal(new Rectangle(280, 200, 120, 200), g.Selection);
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
    }

    private static SelectionGesture RectangleGesture() => new(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], []);
}
