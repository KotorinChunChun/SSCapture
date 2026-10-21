using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionGuideTests()
    {
        Run("選択ガイドは新旧設定で既定ON・OFFと再表示を保存", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            Check(new AppSettings().ShowSelectionGuide && new CaptureOptions(CaptureMode.Rectangle,
                CaptureIntent.Screenshot, OutputDestination.None).ShowSelectionGuide, "新規設定がOFF");
            File.WriteAllText(path, "{}");
            Check(store.Load(out _).ShowSelectionGuide, "旧設定がOFF");
            foreach (bool enabled in new[] { false, true })
            {
                store.Save(new AppSettings { ShowSelectionGuide = enabled, Grid20 = true });
                AppSettings restored = store.Load(out _);
                Equal(enabled, restored.ShowSelectionGuide);
                Check(restored.Grid20, "無関係な設定が失われた");
            }
        }));
        Run("案内の×は表示OFFだけを保存し選択セッションを維持", () => WithServiceDirectory(directory =>
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            int saves = 0;
            var session = GuideTestSession(true, enabled =>
            {
                saves++;
                store.Save(new AppSettings { ShowSelectionGuide = enabled });
            });
            try
            {
                using var guide = new SelectionGuideForm(new(0, 0, 1000, 700), CaptureIntent.Screenshot,
                    session.Cancel, dismiss: () => session.ChangeGuideVisibility(false));
                _ = guide.Handle;
                guide.Close();
                Check(!guide.IsDisposed && !guide.Visible, "案内だけの非表示にならない");
                Check(!session.Completion.IsCompleted, "×で撮影が終了した");
                Check(!session.ShowSelectionGuide && !store.Load(out _).ShowSelectionGuide, "OFFが保持されない");
                Equal(1, saves);
                session.ChangeMode(CaptureMode.Freeform);
                Check(!session.ShowSelectionGuide, "モード変更で再表示された");
                var item = (ToolStripMenuItem)session.SelectionMenu.Command("ShowSelectionGuide")!;
                Check(!item.Checked, "右メニューがOFFにならない");
                item.PerformClick();
                Check(item.Checked && session.ShowSelectionGuide && store.Load(out _).ShowSelectionGuide, "右メニューから再表示できない");
                Check(!session.Completion.IsCompleted, "再表示で撮影が終了した");
                Equal(2, saves);
                var next = GuideTestSession(store.Load(out _).ShowSelectionGuide);
                try { Check(next.ShowSelectionGuide, "次回の表示設定を引き継がない"); }
                finally { next.Cancel(); }
            }
            finally { session.Cancel(); }
        }));
        Run("OFFで開始した案内はモード変更でもOFF・メニューからONに復帰", () =>
        {
            var session = GuideTestSession(false);
            try
            {
                session.ChangeMode(CaptureMode.FixedRegion);
                var item = (ToolStripMenuItem)session.SelectionMenu.Command("ShowSelectionGuide")!;
                Check(!item.Checked && !session.ShowSelectionGuide, "モード変更でONになった");
                item.PerformClick();
                Check(item.Checked && session.ShowSelectionGuide, "メニューでONにならない");
                session.MoveTo(new(180, 170));
                Check(!session.Completion.IsCompleted, "表示切替で選択が終了した");
            }
            finally { session.Cancel(); }
        });
        Run("選択ガイドの破棄は表示設定の保存も撮影キャンセルも行わない", () =>
        {
            int cancelled = 0, dismissed = 0;
            var guide = new SelectionGuideForm(new(0, 0, 1000, 700), CaptureIntent.Screenshot,
                () => cancelled++, dismiss: () => dismissed++);
            _ = guide.Handle;
            guide.Dispose();
            Equal(0, cancelled); Equal(0, dismissed);
        });
        Run("案内の近接判定はドラッグ中のみ・負座標とDPIと復帰幅に対応", () =>
        {
            Rectangle bounds = new(-600, 100, 400, 150);
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                int hideMargin = (int)Math.Ceiling(24 * dpi / 96.0);
                int restoreMargin = (int)Math.Ceiling(40 * dpi / 96.0);
                Point near = new(bounds.Left - hideMargin + 1, bounds.Top);
                Check(SelectionGuideForm.ShouldHideForPointer(bounds, near, true, dpi, false), "近接で隠れない");
                Check(!SelectionGuideForm.ShouldHideForPointer(bounds, near, false, dpi, false), "ドラッグ外でも隠れる");
                Point between = new(bounds.Left - hideMargin - 1, bounds.Top);
                Check(!SelectionGuideForm.ShouldHideForPointer(bounds, between, true, dpi, false), "範囲外で隠れる");
                Check(SelectionGuideForm.ShouldHideForPointer(bounds, between, true, dpi, true), "復帰境界でちらつく");
                Check(!SelectionGuideForm.ShouldHideForPointer(bounds,
                    new(bounds.Left - restoreMargin - 1, bounds.Top), true, dpi, true), "離れても復帰しない");
                Check(!SelectionGuideForm.ShouldHideForPointer(bounds, near, false, dpi, true), "ドラッグ終了後も隠れる");
            }
        });
        Run("案内はドラッグ中だけ隠れ位置も手動OFF設定も変えない", () =>
        {
            int saved = 0, cancelled = 0;
            using var guide = new SelectionGuideForm(new(0, 0, 1000, 700), CaptureIntent.Screenshot,
                () => cancelled++, dismiss: () => saved++);
            _ = guide.Handle;
            guide.Show();
            Point near = guide.Bounds.Location;
            guide.UpdatePointer(near, false);
            Check(guide.Visible && guide.Location == near, "ドラッグ外で隠れた・逃げた");
            guide.UpdatePointer(near, true);
            Check(!guide.Visible && guide.Location == near, "ドラッグ中に隠れない・逃げた");
            guide.UpdatePointer(new(guide.Left - 200, guide.Top), true);
            Check(guide.Visible, "離れても復帰しない");
            guide.UpdatePointer(near, true); Check(!guide.Visible, "再近接で隠れない");
            guide.UpdatePointer(near, false); Check(guide.Visible, "ドラッグ終了で復帰しない");
            Equal(0, saved); Equal(0, cancelled);
        });
        Run("案内の表示設定保存が失敗しても撮影を終了しない", () =>
        {
            var session = GuideTestSession(true, _ => throw new IOException("保存拒否"));
            try
            {
                session.ChangeGuideVisibility(false);
                Check(session.ShowSelectionGuide, "保存失敗をOFFの成功と扱った");
                Check(!session.Completion.IsCompleted, "保存失敗で撮影を終了した");
                Check(((ToolStripMenuItem)session.SelectionMenu.Command("ShowSelectionGuide")!).Checked, "メニュー表示が不一致");
            }
            finally { session.Cancel(); }
        });
    }

    private static SessionController GuideTestSession(bool visible, Action<bool>? save = null)
    {
        Rectangle bounds = new(0, 0, 1000, 700);
        var snapshot = new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]);
        return new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Recording, OutputDestination.None,
            ShowSelectionGuide: visible), snapshot, [], initialPointer: new(100, 100), guideChanged: save);
    }
}
