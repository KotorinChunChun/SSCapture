using System.Drawing.Imaging;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void PreparationTests()
    {
        Run("撮影準備は遷移抑止・非表示・描画同期の順で取得を許可", () =>
        {
            using var form = new Form(); form.Show();
            var steps = new List<string>();
            form.VisibleChanged += (_, _) => { if (!form.Visible) steps.Add("hide"); };
            CaptureUiPreparation.Prepare([form], () => steps.Add("extra"),
                disableTransitions: f => { Check(f.Visible, "抑止前に非表示になった"); steps.Add("disable"); },
                synchronize: () => { Check(!form.Visible, "表示中に同期した"); steps.Add("sync"); });
            steps.Add("capture"); Equal("disable,hide,extra,sync,capture", string.Join(',', steps));
        });
        Run("撮影準備は同期失敗を隠さず取得を止める", () =>
        {
            using var form = new Form(); form.Show(); bool captured = false;
            try
            {
                CaptureUiPreparation.Prepare([form], () => { }, disableTransitions: _ => { }, synchronize: () => throw new IOException("同期fixture"));
                captured = true;
            }
            catch (IOException) { }
            Check(!captured && !form.Visible, "同期失敗後も撮影した");
        });
        Run("表示画面なしの撮影準備は不要な描画待ちを省く", () =>
        {
            int synchronized = 0;
            using var form = new Form();
            CaptureUiPreparation.Prepare([form, null], () => { }, synchronize: () => synchronized++);
            Equal(0, synchronized);
            CaptureUiPreparation.Prepare([], () => { }, syncRequired: true, synchronize: () => synchronized++);
            Equal(1, synchronized);
        });
        Run("撮影後は設定の同じ画面と編集中の値を再表示する", () =>
        {
            using var settings = new SettingsForm(new AppSettings { ShowSelectionGuide = false, ShowPointerGuide = true }, _ => null, () => { });
            settings.Show(); settings.SelectCategory("基本");
            var pointer = (CheckBox)settings.Controls.Find("ShowPointerGuide", true).Single();
            var screen = (CheckBox)settings.Controls.Find("ShowSelectionGuide", true).Single();
            Check(pointer.Checked && !screen.Checked, "ガイドが同じ設定を参照している");
            pointer.Checked = false; screen.Checked = true;
            nint originalHandle = settings.Handle;
            using var previous = new Form(); previous.Show();
            using var hidden = new Form();
            Form[] visible = [settings, previous];
            CaptureUiPreparation.Prepare(visible, () => { }, disableTransitions: _ => { }, synchronize: () => { });
            Check(!settings.Visible && !previous.Visible, "撮影準備で表示中のウィンドウが隠れない");
            CaptureUiPreparation.RestoreAll(visible);
            Check(previous.Visible && !hidden.Visible, "撮影前の表示状態を復元できない");
            Check(settings.Visible && settings.Handle == originalHandle && !pointer.Checked && screen.Checked, "同じ設定画面を復元できない");
        });
        Run("撮影中に変えた設定を同期し、設定画面の編集中の値を残す", () =>
        {
            AppSettings? saved = null;
            var original = new AppSettings { Grid20 = false, ShowSelectionGuide = true };
            using var form = new SettingsForm(original, next => { saved = next; return null; }, () => { });
            var grid = (CheckBox)form.Controls.Find("GridEnabled", true).Single();
            var pointer = (CheckBox)form.Controls.Find("ShowPointerGuide", true).Single();
            pointer.Checked = false;
            form.UpdateCapturePreferences(original with { Grid20 = true, ShowSelectionGuide = false });
            Check(grid.Checked && !pointer.Checked, "撮影中の変更または編集中の値が欠落した");
            SaveConsolidation(form);
            Check(saved is { Grid20: true, ShowSelectionGuide: false, ShowPointerGuide: false }, "古い値で撮影中の変更を上書きした");
        });
        Run("設定画面を隠した後のフォーカス移動中も撮影画面だけがWinを受け取れる", () =>
        {
            using var form = new Form();
            form.Show();
            CaptureUiPreparation.Prepare([form], () => { }, disableTransitions: _ => { }, synchronize: () => { });
            Rectangle bounds = new(0, 0, 360, 240);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                ShowSelectionGuide: false, LimitToStartingMonitor: false),
                new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]), [], captureWindowsKey: true);
            try
            {
                session.Start();
                var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single(window => window.SelectionMenu == session.SelectionMenu);
                nint input = overlay.Handle;
                Check(session.OwnsCapturePointer(0, input), "フォーカスが切り替わる間のWin入力が失われる");
                Check(!session.OwnsCapturePointer(0, form.Handle), "隠した設定画面へWin入力が送られる");
                using var unrelated = new Form();
                Check(!session.OwnsCapturePointer(unrelated.Handle, input), "撮影開始時と異なる前面ウィンドウからWinを受け取った");
                session.Cancel();
                Check(!session.OwnsCapturePointer(0, input), "終了した撮影がWinを受け取る");
            }
            finally
            {
                session.Cancel();
                CaptureUiPreparation.Restore(form);
            }
        });
    }

    private static int RunSettingsWindowsKeySmoke()
    {
        var original = new AppSettings { ShowSelectionGuide = false, ShowPointerGuide = true };
        AppSettings? saved = null;
        using var settings = new SettingsForm(original, value => { saved = value; return null; }, () => { });
        Exception? failure = null;
        settings.Shown += (_, _) => settings.BeginInvoke((Action)(async () =>
        {
            try
            {
                using var input = new NativeSmokeInput();
                NativeSmokeInput.FocusFixture(settings.Handle);
                NativeSmokeInput.RequireForeground(settings.Handle);
                var guide = (CheckBox)settings.Controls.Find("ShowPointerGuide", true).Single();
                guide.Checked = false;
                SaveConsolidation(settings);
                Check(saved is { ShowPointerGuide: false }, "設定を保存できない");
                Point location = Screen.FromControl(settings).WorkingArea.Location + new Size(80, 80);
                Rectangle bounds = new(location, new Size(360, 240));
                Point pointer = location + new Size(90, 70);
                CaptureUiPreparation.Prepare([settings], () => { });
                input.Move(pointer);
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                    ShowSelectionGuide: false, LimitToStartingMonitor: false, OutputSettings: saved),
                    new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]), [],
                    initialPointer: pointer, captureWindowsKey: true);
                try
                {
                    session.Start();
                    var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single(window => window.SelectionMenu == session.SelectionMenu);
                    NativeSmokeInput.RequirePointTarget(overlay.Handle, pointer);
                    Check(CaptureNative.OwnsForegroundWindow() || session.OwnsCapturePointer(CaptureNative.GetForegroundWindow(), overlay.Handle),
                        "撮影画面へWin入力を送れないため中止");
                    input.KeyDown(0x5B);
                    await WaitUntil(() => session.Mode == CaptureMode.Window, "設定を編集した直後のWin押下が届かない");
                    input.KeyUp(0x5B);
                    await WaitUntil(() => session.Mode == CaptureMode.Rectangle, "設定を編集した直後のWin解放が届かない");
                }
                finally
                {
                    session.Cancel();
                    CaptureUiPreparation.Restore(settings);
                }
                Check(settings.Visible && !guide.Checked, "設定画面や変更した値が復元されない");
                Console.WriteLine("SETTINGS WIN PASS 設定保存・非表示の直後のWin押下と解放、設定画面の復元");
            }
            catch (Exception error) { failure = error; }
            finally { settings.Close(); }
        }));
        Application.Run(settings);
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    // 明示実行専用。自作画面の範囲だけを取得し、隠した設定画面の残像を検査する。
    private static int PreparationSmoke(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        var target = new Rectangle(work.Right - Math.Min(work.Width, 980), work.Top, Math.Min(work.Width, 980), Math.Min(work.Height, 1000));
        using var pattern = new Form { Text = WindowTitle.Create("撮影準備検証"), FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = target, BackColor = Color.FromArgb(37, 79, 131), TopMost = true };
        Exception? failure = null;
        pattern.Shown += (_, _) => pattern.BeginInvoke((Action)(() =>
        {
            try
            {
                pattern.Update();
                using var settings = new SettingsForm(new AppSettings(), _ => null, () => { });
                settings.StartPosition = FormStartPosition.Manual; settings.Location = new Point(target.Left + 12, target.Top + 12);
                settings.TopMost = true; settings.Show(); settings.Update(); CaptureNative.SynchronizeDesktop();
                CaptureUiPreparation.Prepare([settings], () => { });
                using var image = InstantCapture.Take(target);
                image.Save(Path.Combine(directory, "after-hide.png"), ImageFormat.Png);
                int different = 0;
                for (int y = 2; y < image.Height - 2; y++)
                    for (int x = 2; x < image.Width - 2; x++)
                        if (image.GetPixel(x, y).ToArgb() != pattern.BackColor.ToArgb()) different++;
                File.WriteAllText(Path.Combine(directory, "result.txt"), $"取得範囲={target}; 残像画素={different}");
                if (different != 0) throw new IOException($"設定画面の残像等が{different}画素あります。");
                Console.WriteLine($"PREPARATION PASS: {target}; residualPixels=0");
            }
            catch (Exception ex) { failure = ex; }
            finally { pattern.Close(); }
        }));
        Application.Run(pattern);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
}
