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
        Run("入力プリセットを開いた設定の子画面にはアニメーション抑止や再表示を行わない", () =>
        {
            using var settings = new SettingsForm(new AppSettings(), _ => null, _ => { });
            settings.Show();
            settings.SelectCategory("入力プリセット");
            var embedded = Application.OpenForms.OfType<RegionManagerForm>().Single(form => settings.Contains(form));
            Check(embedded.Visible && !embedded.TopLevel, "埋め込み画面が表示されていない");
            Form[] visible = [embedded, settings];
            Check(CaptureUiPreparation.VisibleTopLevelWindows(visible).SequenceEqual([settings]), "復元一覧に子画面が含まれた");
            CaptureUiPreparation.Prepare(visible, () => { }, disableTransitions: window => Equal<Form>(settings, window),
                synchronize: () => Check(!embedded.Visible, "親を隠した後も子画面が表示されている"));
            CaptureUiPreparation.RestoreAll(visible);
            Check(settings.Visible && embedded.Visible, "入力プリセットを同じ状態に復元できない");
        });
        Run("非表示を維持できない場合は設定を閉じて再撮影するよう案内する", () =>
        {
            using var form = new Form(); form.Show();
            IOException? error = null;
            bool synchronized = false;
            try
            {
                CaptureUiPreparation.Prepare([form], form.Show, disableTransitions: _ => { },
                    synchronize: () => synchronized = true);
            }
            catch (IOException ex) { error = ex; }
            Check(error is not null && error.Message.Contains("撮影前に自分の画面を非表示にできません。") &&
                error.Message.Contains("設定画面を閉じてください。その後、もう一度撮影してください。"), "非表示失敗の理由か再撮影の案内が欠けている");
            Check(!synchronized, "表示中の画面を同期した");
        });
        Run("撮影準備は同期失敗を隠さず取得を止める", () =>
        {
            using var form = new Form(); form.Show();
            var cause = new IOException("同期fixture");
            IOException? error = null;
            try
            {
                CaptureUiPreparation.Prepare([form], () => { }, disableTransitions: _ => throw TransitionFailure(),
                    synchronize: () => throw cause);
            }
            catch (IOException ex) { error = ex; }
            Check(!form.Visible, "抑止に失敗した後で設定を隠していない");
            Check(error is not null && error.InnerException == cause &&
                error.Message.Contains("設定画面を閉じてください。その後、もう一度撮影してください。"), "同期失敗の理由か再撮影の案内が欠けている");
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
        Run("撮影後は設定の同じ画面と編集中の値を再表示する", () => VerifySettingsRestored(false));
        Run("アニメーション抑止に失敗しても、非表示と描画同期後に設定を復元する", () => VerifySettingsRestored(true));

        void VerifySettingsRestored(bool simulateTransitionFailure)
        {
            using var settings = new SettingsForm(new AppSettings { ShowSelectionGuide = false, ShowPointerGuide = true }, _ => null, _ => { });
            settings.Show(); settings.SelectCategory("基本");
            var pointer = (CheckBox)settings.Controls.Find("ShowPointerGuide", true).Single();
            var screen = (CheckBox)settings.Controls.Find("ShowSelectionGuide", true).Single();
            Check(pointer.Checked && !screen.Checked, "ガイドが同じ設定を参照している");
            pointer.Checked = false; screen.Checked = true;
            nint originalHandle = settings.Handle;
            using var previous = new Form(); previous.Show();
            using var hidden = new Form();
            Form[] visible = [settings, previous];
            var attempted = new List<Form>();
            int syncs = 0;
            CaptureUiPreparation.Prepare(visible, () => { }, disableTransitions: window =>
            {
                attempted.Add(window);
                if (simulateTransitionFailure && window == settings) throw TransitionFailure();
            }, synchronize: () =>
            {
                Check(!settings.Visible && !previous.Visible, "表示中に同期した");
                syncs++;
            });
            Check(attempted.SequenceEqual(visible), "抑止に失敗した後のウィンドウを処理していない");
            Equal(1, syncs);
            Check(!settings.Visible && !previous.Visible, "撮影準備で表示中のウィンドウが隠れない");
            CaptureUiPreparation.RestoreAll(visible);
            Check(previous.Visible && !hidden.Visible, "撮影前の表示状態を復元できない");
            Check(settings.Visible && settings.Handle == originalHandle && !pointer.Checked && screen.Checked, "同じ設定画面を復元できない");
        }
        Run("撮影中に変えた設定を同期し、設定画面の編集中の値を残す", () =>
        {
            AppSettings? saved = null;
            var original = new AppSettings { Grid20 = false, ShowSelectionGuide = true };
            using var form = new SettingsForm(original, next => { saved = next; return null; }, _ => { });
            var grid = (CheckBox)form.Controls.Find("GridEnabled", true).Single();
            var pointer = (CheckBox)form.Controls.Find("ShowPointerGuide", true).Single();
            pointer.Checked = false;
            form.UpdateCapturePreferences(original with { Grid20 = true, ShowSelectionGuide = false });
            Check(grid.Checked && !pointer.Checked, "撮影中の変更または編集中の値が欠落した");
            ApplySettingsPage(form, "基本");
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
        using var settings = new SettingsForm(original, value => { saved = value; return null; }, _ => { });
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
                ApplySettingsPage(settings, "基本");
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

    private static IOException TransitionFailure() =>
        new("撮影前の画面アニメーションを抑止できません。", new UnauthorizedAccessException());

    // 明示実行専用。自作画面の範囲だけを取得し、隠した設定画面の残像を検査する。
    private static int PreparationSmoke(string directory, bool simulateTransitionFailure)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var pattern = new Form { Text = WindowTitle.Create("撮影準備検証"), FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, BackColor = Color.FromArgb(37, 79, 131), TopMost = true };
        Exception? failure = null;

        int CountDifferentPixels(Rectangle target, string? imagePath = null)
        {
            using var image = InstantCapture.Take(target);
            int different = 0;
            int expected = pattern.BackColor.ToArgb();
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (image.GetPixel(x, y).ToArgb() != expected) different++;
            if (different == 0 && imagePath is not null) image.Save(imagePath, ImageFormat.Png);
            return different;
        }

        Rectangle FindFixtureTarget()
        {
            foreach (var screen in Screen.AllScreens.OrderBy(screen => screen.Primary))
            {
                Rectangle work = screen.WorkingArea;
                pattern.Bounds = new Rectangle(work.Left + work.Width / 3, work.Top + work.Height / 3, 280, 220);
                pattern.Refresh();
                CaptureNative.SynchronizeDesktop();
                var sample = new Rectangle(pattern.PointToScreen(new Point(40, 60)), new Size(120, 80));
                if (CountDifferentPixels(sample) == 0) return sample;
            }
            throw new IOException("検証用の背景が見える撮影範囲を確保できません。");
        }

        pattern.Shown += (_, _) => pattern.BeginInvoke((Action)(() =>
        {
            try
            {
                Rectangle target = FindFixtureTarget();
                int commits = 0;
                using var settings = new SettingsForm(new AppSettings(), _ => { commits++; return null; }, _ => { });
                settings.StartPosition = FormStartPosition.Manual;
                settings.Location = new Point(target.Left - 24, target.Top - 48);
                settings.TopMost = true; settings.Show();
                var tabs = (TabControl)settings.Controls.Find("SettingsTabs", true).Single();
                var results = new List<string>();
                foreach (TabPage page in tabs.TabPages)
                {
                    settings.SelectCategory(page.Text);
                    int before = commits;
                    ChangePreparationSetting(settings, page.Text);
                    if (page.Text is not ("コマンド生成" or "バージョン情報"))
                        Check(commits > before, $"{page.Text}の設定変更を保存できない");
                    settings.Update();
                    CaptureNative.SynchronizeDesktop();
                    Check(settings.Bounds.Contains(target) && CountDifferentPixels(target) > 0, "撮影前に設定画面が検証範囲を覆っていない");
                    var windows = Application.OpenForms.OfType<Form>().Where(form => form != pattern);
                    Form[] toRestore = CaptureUiPreparation.VisibleTopLevelWindows(windows);
                    Check(toRestore.SequenceEqual([settings]), "設定の親ウィンドウ以外が復元対象に含まれた");
                    try
                    {
                        CaptureUiPreparation.Prepare(windows, () => { },
                            disableTransitions: simulateTransitionFailure ? _ => throw TransitionFailure() : null);
                        string? imagePath = page.Text == "入力プリセット" ? Path.Combine(directory, "after-hide.png") : null;
                        int different = CountDifferentPixels(target, imagePath);
                        if (different != 0) throw new IOException($"設定画面の残像等が{different}画素あります。");
                    }
                    catch (IOException ex) { throw new IOException($"{page.Text}の撮影準備: {ex.Message}", ex); }
                    finally { CaptureUiPreparation.RestoreAll(toRestore); }
                    Check(settings.Visible, "設定画面を復元できない");
                    Equal(page.Text, settings.SelectedCategory);
                    results.Add($"{page.Text}: 残像画素=0、設定画面を復元");
                    Console.WriteLine(results[^1]);
                }
                File.WriteAllLines(Path.Combine(directory, "result.txt"), results);
                Console.WriteLine($"PREPARATION PASS: {results.Count} pages");
            }
            catch (Exception ex) { failure = ex; }
            finally { pattern.Close(); }
        }));
        Application.Run(pattern);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static void ChangePreparationSetting(SettingsForm settings, string category)
    {
        void Toggle(string name)
        {
            var check = (CheckBox)settings.Controls.Find(name, true).Single();
            check.Checked = !check.Checked;
        }

        switch (category)
        {
            case "基本": Toggle("DimCaptureBackground"); ApplySettingsPage(settings, category); break;
            case "通知": Toggle("PlayCaptureSound"); ApplySettingsPage(settings, category); break;
            case "履歴": Toggle("KeepHistory"); ApplySettingsPage(settings, category); break;
            case "入力プリセット":
                settings.Controls.Find("RegionName", true).Single().Text = "撮影準備の入力";
                ConsolidationClick(settings, "SaveRegion");
                break;
            case "出力プリセット":
                settings.Controls.Find("WhitePaddingPixels", true).Single().Text = "11";
                ConsolidationClick(settings, "StoreCapturePreset");
                break;
            case "ショートカット":
                var groups = (TabControl)settings.Controls.Find("ShortcutGroups", true).Single();
                groups.SelectedTab = groups.TabPages.Cast<TabPage>().Single(page => page.Text == "2点目");
                Toggle("CaptureKeysWheelAdjustRectangle");
                ApplySettingsPage(settings, category, "2点目");
                break;
            case "外部アプリ":
                ConsolidationClick(settings, "ImageEditorDuplicate");
                ConsolidationClick(settings, "ImageEditorStore");
                break;
        }
    }
}
