using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    // 多段メニューの項目座標はルートではなく、実際に所有するポップアップを基準にする。
    private static ToolStripDropDown RevealMenuCommand(CaptureContextMenu menu, ToolStripMenuItem item)
    {
        bool Reveal(ToolStripMenuItem parent)
        {
            if (parent.DropDownItems.Contains(item)) { parent.ShowDropDown(); return true; }
            foreach (var child in parent.DropDownItems.OfType<ToolStripMenuItem>())
            {
                if (!Contains(child)) continue;
                parent.ShowDropDown(); return Reveal(child);
            }
            return false;
        }
        bool Contains(ToolStripMenuItem parent) => parent.DropDownItems.Contains(item) || parent.DropDownItems.OfType<ToolStripMenuItem>().Any(Contains);
        foreach (var root in menu.Items.OfType<ToolStripMenuItem>()) if (Reveal(root)) break;
        return (ToolStripDropDown)item.GetCurrentParent()!;
    }
    private static DesktopSnapshot MenuFrame(Color color, int width = 640)
    {
        var image = new Bitmap(width, 480);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        return new(image, new(0, 0, width, 480), [new(0, 0, width, 480)]);
    }
    private static SessionController MenuSession(Func<bool, bool, DesktopSnapshot>? recapture = null,
        CaptureIntent intent = CaptureIntent.Screenshot, AppSettings? settings = null) => new(
        new(CaptureMode.FixedRegion, intent, OutputDestination.File, FixedSize: new(100, 80),
            OutputSettings: settings, ShowSelectionGuide: false), MenuFrame(Color.Red), [], initialPointer: new(40, 40), recapture: recapture);

    private static void CaptureMenuEditingTests()
    {
        Run("通知領域のサブメニューは親モニター内で右・左・下端を選ぶ", () =>
        {
            Rectangle leftMonitor = new(-1920, -120, 1920, 1040);
            Size menuSize = new(280, 160);
            Equal(new Point(-540, 760), TraySubmenuItem.Place(new(-261, 890, 260, 24), menuSize, leftMonitor));
            Equal(new Point(-1661, -110), TraySubmenuItem.Place(new(-1900, -110, 240, 24), menuSize, leftMonitor));
            Equal(leftMonitor.Location, TraySubmenuItem.Place(new(-1100, 200, 260, 24), new(2100, 1300), leftMonitor));
            Rectangle rightMonitor = new(0, 80, 2560, 1360);
            Equal(new Point(560, 1280), TraySubmenuItem.Place(new(301, 1400, 260, 24), menuSize, rightMonitor));
        });
        Run("実際のWinFormsサブメニューを各モニターの右端で開き直しても同じ画面に収まる", () =>
        {
            using var menu = new ContextMenuStrip();
            var parent = new TraySubmenuItem("プリセット撮影");
            int clicks = 0;
            parent.DropDownItems.Add("撮影する", null, (_, _) => clicks++);
            menu.Items.Add(parent);
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle work = screen.WorkingArea;
                menu.Show(new Point(work.Right - Math.Min(400, work.Width), work.Top + 30));
                try
                {
                    // Use the width after layout on this monitor, including its DPI.
                    menu.Location = new Point(work.Right - menu.Width, menu.Top);
                    Equal(screen.DeviceName, Screen.FromControl(menu).DeviceName);
                    parent.ShowDropDown();
                    Check(parent.DropDown.Visible, "サブメニューが開かない");
                    Equal(Screen.FromControl(menu).DeviceName, Screen.FromControl(parent.DropDown).DeviceName);
                    Check(work.Contains(parent.DropDown.Bounds),
                        $"対象={screen.DeviceName} 作業領域={work} 親={menu.Bounds} 子={parent.DropDown.Bounds}");
                    Rectangle anchor = menu.RectangleToScreen(parent.Bounds);
                    Check(parent.DropDown.Right <= anchor.Left + 1, "右端では親の左に開く");
                    parent.DropDownItems[0].PerformClick();
                }
                finally { menu.Close(); }
            }
            Equal(Screen.AllScreens.Length, clicks);
        });
        Run("通知領域の区切りは表示中の操作の間だけに出る", () =>
        {
            using var menu = new ContextMenuStrip();
            var start = new ToolStripSeparator();
            var first = new ToolStripMenuItem("静止画");
            var middle = new ToolStripSeparator();
            var conditional = new ToolStripMenuItem("結果") { Available = false };
            var next = new ToolStripSeparator();
            var last = new ToolStripMenuItem("終了");
            var end = new ToolStripSeparator();
            menu.Items.AddRange([start, first, middle, conditional, next, last, end]);
            TrayMenuShortcuts.UpdateSeparators(menu.Items);
            Check(!start.Available && middle.Available && !next.Available && !end.Available, "先頭・末尾または連続した区切りが残った");
            conditional.Available = true;
            TrayMenuShortcuts.UpdateSeparators(menu.Items);
            Check(middle.Available && next.Available, "操作が再表示されたまとまりに区切りがない");
        });

        Run("通知領域のキー表示は既定の撮影と同じ対象・出力の割り当てだけを併記する", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var input = defaults.SavedRegions.Single(p => p.IsDefault);
            var other = defaults.SavedRegions.First(p => !p.IsDefault);
            var output = defaults.CapturePresets.Single(p => p.IsDefault);
            var otherOutput = defaults.CapturePresets.First(p => !p.IsDefault);
            PresetCaptureShortcut[] rows =
            [
                new("Ctrl+F5") { SelectRangeWithDefaultInput = true },
                new("Ctrl+F6", input.Id, output.Id),
                new("Ctrl+F7", other.Id),
                new("Ctrl+F8", OutputPresetId: otherOutput.Id),
                new("Ctrl+F9", Intent: CaptureIntent.Recording),
                new("Ctrl+F10", other.Id, Intent: CaptureIntent.Recording)
            ];
            var settings = defaults with { PresetCaptureShortcuts = rows };
            Equal("Ctrl＋F5 / Ctrl＋F6", TrayMenuShortcuts.Capture(settings, CaptureIntent.Screenshot));
            Equal("Ctrl＋F9", TrayMenuShortcuts.Capture(settings, CaptureIntent.Recording));
            Equal("Ctrl＋F9 / Ctrl＋F10", TrayMenuShortcuts.Capture(settings, CaptureIntent.Recording, recordingActive: true));
            Equal("Ctrl＋F7", TrayMenuShortcuts.Region(settings, other));
            var cleared = settings with { PresetCaptureShortcuts = [] };
            Equal("", TrayMenuShortcuts.Capture(cleared, CaptureIntent.Screenshot));
            Equal("", TrayMenuShortcuts.Format([null, "", " "]));
            Equal("Ctrl＋F5", TrayMenuShortcuts.Format(["Ctrl+F5", "CTRL+F5"]));
        });

        Run("ウィンドウ撮影時だけ取得方式を選べる", () =>
        {
            bool? isolated = null;
            using var menu = new CaptureContextMenu(OutputDestination.Clipboard, _ => { }, () => { }, () => { },
                changeWindowSource: value => isolated = value);
            var source = menu.Command("WindowCaptureSource");
            Check(!source.Available, "矩形撮影にウィンドウ撮影方式が出た");
            menu.SetWindowSource(CaptureMode.Window, false);
            Check(source.Available && menu.Command("VisibleWindowCapture").Checked, "画面切り抜きが初期選択されていない");
            menu.Command("IsolatedWindowCapture").PerformClick();
            Equal<bool?>(true, isolated);
            menu.SetWindowSource(CaptureMode.Window, true);
            Check(menu.Command("IsolatedWindowCapture").Checked && !menu.Command("VisibleWindowCapture").Checked, "WGCのチェックが不一致");
        });
        Run("撮影メニューのルートは2プリセットと一時カスタマイズ・表示操作", () =>
        {
            var session = MenuSession();
            try
            {
                var menu = session.SelectionMenu;
                Equal("InputPresetMenu,CapturePresetMenu,CaptureRangeGroup,CaptureOutputGroup,CaptureOperationGroup,OpenSettings,CancelCapture",
                    string.Join(',', menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Name)));
                foreach (string name in new[] { "CaptureModeMenu", "FixedAnchorMenu", "FreezeFrame", "IncludeCursor" })
                    Check(menu.Command("CaptureRangeGroup").DropDownItems.Contains(menu.Command(name)), name + "の分類不一致");
                foreach (string name in new[] { "ScreenshotColorDepthMenu", "MetadataModeMenu", "ScreenshotPaddingMenu" })
                    Check(menu.Command("CaptureOutputGroup").DropDownItems.Contains(menu.Command(name)), name + "の分類不一致");
                foreach (string name in new[] { "LimitToStartingMonitor", "DimCaptureBackground", "Grid20", "ShowSelectionGuide", "ShowPointerGuide" })
                    Check(menu.Command("CaptureOperationGroup").DropDownItems.Contains(menu.Command(name)), name + "の分類不一致");
                Equal("開始したモニター内で選択する", menu.Command("LimitToStartingMonitor").Text);
                Equal("選択中モニターの背景を暗くする", menu.Command("DimCaptureBackground").Text);
                Equal("グリッドスナップを既定でON（20px）", menu.Command("Grid20").Text);
                Equal("画面右下に操作ガイドを表示する", menu.Command("ShowSelectionGuide").Text);
                Check(!menu.Command("FreezeFrame").Enabled && !menu.Command("IncludeCursor").Enabled, "再取得不可の入口を有効にした");
            }
            finally { session.Cancel(); }
        });
        Run("減色・暗転・基点は今回だけ適用してメニューと出力へ反映", () =>
        {
            var original = new AppSettings { DimCaptureBackground = false }; var session = MenuSession(settings: original);
            try
            {
                session.MoveTo(new(100, 100));
                session.SelectionMenu.Command("ColorIndexed4").PerformClick();
                session.SelectionMenu.Command("DimCaptureBackground").PerformClick();
                session.SelectionMenu.Command("AnchorBottomRight").PerformClick();
                Equal(ScreenshotColorDepth.Indexed4, session.OutputSettings.ScreenshotColorDepth);
                Check(session.DimCaptureBackground && session.SelectionMenu.Command("ColorIndexed4").Checked, "表示と設定が不一致");
                Equal(CaptureAnchor.BottomRight, session.FixedAnchor);
                Check(!original.DimCaptureBackground && original.ScreenshotColorDepth != ScreenshotColorDepth.Indexed4, "元設定を変更した");
                session.ChangeFixedSize(new(321, 123)); session.MoveTo(new(400, 300));
                session.HandleCommandKey(Keys.Enter);
                WaitHistory(session.Completion);
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(ScreenshotColorDepth.Indexed4, outcome.OutputSettings!.ScreenshotColorDepth);
                Equal(new Size(321, 123), outcome.Selection!.Bounds.Size);
            }
            finally { session.Cancel(); }
        });
        Run("任意固定寸法は位置維持・基点切替・過大面積の拒否", () =>
        {
            var session = MenuSession();
            try
            {
                session.MoveTo(new(100, 100)); var location = session.Selection.Location;
                session.ChangeFixedSize(new(321, 123)); Equal(new Rectangle(location, new(321, 123)), session.Selection);
                try { session.ChangeFixedSize(new(32768, 32768)); throw new Exception("過大寸法を受理した"); } catch (ArgumentException) { }
                Equal(new Size(321, 123), session.Selection.Size);
                foreach (var anchor in Enum.GetValues<CaptureAnchor>())
                { session.ChangeFixedAnchor(anchor); Equal(anchor, session.FixedAnchor); Check(session.SelectionMenu.Command("Anchor" + anchor).Checked, "基点チェック不一致"); }
                session.HandleCommandKey(Keys.Space);
                Check(session.SelectionMenu.Command("Anchor" + session.FixedAnchor).Checked, "Space後の基点チェック不一致");
            }
            finally { session.Cancel(); }
        });
        Run("全階層に入力フォームを埋め込まず数値・保存編集は設定へ集約", () =>
        {
            var session = MenuSession();
            try
            {
                var menu = session.SelectionMenu;
                Check(!menu.Items.OfType<ToolStripControlHost>().Any() && menu.AllCommands.All(item => !item.DropDownItems.OfType<ToolStripControlHost>().Any()), "入力ホストがある");
                Check(!menu.AllCommands.Any(item => item.Name is "FixedSizeMenu" or "CapturePrecisionMenu" or "OutputDetailsMenu"), "旧入力フォームの入口が残る");
                Equal("今回だけ入力を変更", menu.Command("CaptureRangeGroup").Text);
                Equal("今回だけ出力を変更", menu.Command("CaptureOutputGroup").Text);
            }
            finally { session.Cancel(); }
        });
        Run("末端コマンドの適用は親子ポップアップをすべて閉じて選択を続行", () =>
        {
            var session = MenuSession(); using var host = new Form { ShowInTaskbar = false };
            try
            {
                host.Show(); session.SelectionMenu.Show(host, new Point(20, 20));
                session.SelectionMenu.Command("CaptureRangeGroup").ShowDropDown();
                session.SelectionMenu.Command("FixedAnchorMenu").ShowDropDown();
                session.SelectionMenu.Command("AnchorBottomRight").PerformClick();
                Check(!session.Completion.IsCompleted, "未開始の確認セッションをキャンセルした");
                Check(!session.SelectionMenu.Visible && session.SelectionMenu.AllCommands.All(i => !i.HasDropDownItems || !i.DropDown.Visible), "子ポップアップが残る");
            }
            finally { session.Cancel(); }
        });
        Run("フォーカス喪失で右クリックメニューの全階層とキャプチャが閉じる", () =>
        {
            var session = MenuSession();
            using var host = new Form { ShowInTaskbar = false };
            try
            {
                host.Show();
                var menu = session.SelectionMenu;
                menu.Show(host, new Point(20, 20));
                var leaf = RevealMenuCommand(menu, menu.Command("AnchorBottomRight"));
                int VisibleMenus() => (menu.Visible ? 1 : 0)
                    + menu.AllCommands.Count(item => item.HasDropDownItems && item.DropDown.Visible);
                Equal(3, VisibleMenus());
                leaf.Capture = true;
                menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                Equal(0, VisibleMenus());
                Check(!leaf.Capture, "閉じたポップアップがキャプチャを保持");
            }
            finally { session.Cancel(); }
        });
        Run("末端実行は一度だけで閉じるクリックを選択に使わず次の左押下でドラッグできる", () =>
        {
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot,
                OutputDestination.Clipboard, LimitToStartingMonitor: false, ShowSelectionGuide: false), MenuFrame(Color.Red), []);
            try
            {
                session.Start();
                var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single(f => f.SelectionMenu == session.SelectionMenu);
                var menu = session.SelectionMenu;
                var openAt = new Point(45, 45);
                var start = new Point(80, 80);
                var end = new Point(200, 170);
                session.MouseDown(overlay, MouseButtons.Right, openAt);
                var command = menu.Command("ColorIndexed4");
                var popup = RevealMenuCommand(menu, command);
                int closed = 0, clicked = 0;
                menu.Closed += (_, _) => closed++;
                command.Click += (_, _) =>
                {
                    clicked++;
                    Check(!menu.Visible && !popup.Visible && !popup.Capture, "コマンド実行後もポップアップが残る");
                };
                command.PerformClick();
                Equal(1, closed);
                Equal(1, clicked);
                session.MouseDown(overlay, MouseButtons.Left, start);
                Check(session.HasPointerPress, "メニュー終了の遅延処理を待たずに押した左ボタンを落とした");
                session.AbortSelection();
                session.MouseUp(MouseButtons.Left, start);
                session.MouseDown(overlay, MouseButtons.Right, openAt);
                var reopenedPopup = RevealMenuCommand(menu, command);
                Application.DoEvents();
                Check(menu.Visible && reopenedPopup.Visible, "前回の終了通知が再表示したメニューを閉じた");
                menu.Close(ToolStripDropDownCloseReason.AppClicked);
                overlay.Capture = true;
                session.MouseDown(overlay, MouseButtons.Left, start);
                session.MouseUp(MouseButtons.Left, start);
                Check(!session.HasPointerPress && !overlay.Capture && !session.Completion.IsCompleted, "閉じる押下が選択へ伝わった");
                Application.DoEvents();
                session.MouseDown(overlay, MouseButtons.Left, start);
                Check(session.HasPointerPress, "次の左押下が無視された");
                session.MoveTo(end);
                Check(session.Dragging, "次の左押下からドラッグを開始できない");
                session.MouseUp(MouseButtons.Left, end);
                WaitHistory(session.Completion);
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(new Rectangle(80, 80, 120, 90), outcome.Selection!.Bounds);
                Equal(ScreenshotColorDepth.Indexed4, outcome.OutputSettings!.ScreenshotColorDepth);
            }
            finally { session.Cancel(); }
        });
        Run("固定サイズはメニューで基点を選んだ直後の押下で新しいポインタ位置から撮影する", () =>
        {
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot,
                OutputDestination.Clipboard, FixedSize: new(100, 80), LimitToStartingMonitor: false,
                ShowSelectionGuide: false), MenuFrame(Color.Red), []);
            try
            {
                session.Start();
                var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single(f => f.SelectionMenu == session.SelectionMenu);
                session.MouseDown(overlay, MouseButtons.Right, new Point(140, 120));
                var command = session.SelectionMenu.Command("AnchorBottomRight");
                RevealMenuCommand(session.SelectionMenu, command);
                command.PerformClick();
                var confirmAt = new Point(230, 200);
                session.MouseDown(overlay, MouseButtons.Left, confirmAt);
                Check(session.HasPointerPress, "基点選択後の最初の左押下を受け取らなかった");
                session.MouseUp(MouseButtons.Left, confirmAt);
                WaitHistory(session.Completion);
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Equal(CaptureAnchor.BottomRight, session.FixedAnchor);
                Equal(new Rectangle(130, 120, 100, 80), outcome.Selection!.Bounds);
            }
            finally { session.Cancel(); }
        });
        Run("Escとルートの設定は開いたメニューを閉じて撮影せずに終了する", () =>
        {
            foreach (bool openSettings in new[] { false, true })
            {
                var settings = new AppSettings();
                SavedRegion preset = SavedRegion.FromSettings("指定する入力", settings) with { IsDefault = true };
                var session = MenuSession(settings: settings with { SavedRegions = [preset] });
                using var host = new Form { ShowInTaskbar = false };
                try
                {
                    var menu = session.SelectionMenu;
                    host.Show();
                    menu.Show(host, new Point(20, 20));
                    var command = menu.Command("OpenSettings");
                    Check(menu.Items.Contains(command) && !command.HasDropDownItems, "設定が直接開けない");
                    Check(command.ToolTipText?.Contains("撮影を終了") == true, "設定のツールチップがない");
                    var cancel = menu.Command("CancelCapture");
                    Equal("Esc", cancel.ShortcutKeyDisplayString);
                    Equal(Keys.None, cancel.ShortcutKeys);
                    ToolStripDropDown? popup = null;
                    if (openSettings)
                    {
                        int invocations = 0;
                        menu.CommandInvoking += () => { Check(!menu.Visible, "設定の実行前にメニューが閉じていない"); invocations++; };
                        command.PerformClick();
                        Equal(1, invocations);
                    }
                    else
                    {
                        popup = RevealMenuCommand(menu, menu.Command("ModeRectangle"));
                        var escape = Message.Create(popup.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                        Check(popup.PreProcessMessage(ref escape), "子メニューがEscを処理しない");
                    }
                    using var outcome = session.Completion.GetAwaiter().GetResult();
                    Check(!menu.Visible && popup?.Visible != true && outcome.Selection is null && outcome.Image is null, "撮影後のUIが残った");
                    Equal(openSettings, outcome.OpenSettings);
                    Equal<CaptureSettingsLink?>(null, outcome.SettingsLink);
                }
                finally { session.Cancel(); }
            }
        });
        Run("設定リンクは指定した入出力プリセットとショートカット欄を開く", () =>
        {
            var defaults = new AppSettings();
            var input = SavedRegion.FromSettings("用途別入力", defaults);
            var output = CapturePreset.FromSettings("用途別出力", defaults);
            var settings = defaults with
            {
                SavedRegions = [SavedRegion.FromSettings("既定入力", defaults) with { IsDefault = true }, input],
                CapturePresets = [CapturePreset.FromSettings("既定出力", defaults) with { IsDefault = true }, output]
            };
            using var settingsForm = new SettingsForm(settings, _ => null, _ => { }, listMicrophones: () => [], listDisplays: () => []);
            Control Find(string name) => settingsForm.Controls.Find(name, true).Single();
            var tabs = (TabControl)Find("SettingsTabs");

            settingsForm.ShowCaptureSettings(new(CaptureSettingsDestination.InputPreset, input.Id));
            Equal("入力プリセット", tabs.SelectedTab!.Text);
            Equal(input.Id, ((SavedRegion)((ListBox)Find("SavedRegions")).SelectedItem!).Id);
            settingsForm.ShowCaptureSettings(new(CaptureSettingsDestination.OutputPreset, output.Id));
            Equal("出力プリセット", tabs.SelectedTab!.Text);
            Equal(output.Id, ((CapturePreset)((ListBox)Find("CapturePresetList")).SelectedItem!).Id);
            settingsForm.ShowCaptureSettings(new(CaptureSettingsDestination.Shortcut, ShortcutControlName: "CaptureKeysSpace"));
            Equal("ショートカット", tabs.SelectedTab!.Text);
            var shortcutTabs = (TabControl)Find("ShortcutGroups");
            Equal("2点目", shortcutTabs.SelectedTab!.Text);
        });
        Run("操作ガイドと録画音声のチェックは各項目が独立し今回の撮影設定へ渡る", () =>
        {
            var settings = new AppSettings { ShowPointerGuide = true, RecordSystemAudio = false, RecordMicrophone = true };
            var session = MenuSession(intent: CaptureIntent.Recording, settings: settings);
            try
            {
                var menu = session.SelectionMenu;
                var pointerGuide = menu.Command("ShowPointerGuide");
                var bottomGuide = menu.Command("ShowSelectionGuide");
                var systemAudio = menu.Command("RecordSystemAudio");
                var microphone = menu.Command("RecordMicrophone");
                Check(pointerGuide.Checked && !bottomGuide.Checked && !systemAudio.Checked && microphone.Checked, "初期チェックが設定と不一致");
                pointerGuide.PerformClick();
                systemAudio.PerformClick();
                microphone.PerformClick();
                Check(!pointerGuide.Checked && !bottomGuide.Checked && systemAudio.Checked && !microphone.Checked, "別の項目のチェックが連動した");
                session.MoveTo(new(100, 100));
                session.HandleCommandKey(Keys.Enter);
                WaitHistory(session.Completion);
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Check(outcome.OutputSettings is { ShowPointerGuide: false, RecordSystemAudio: true, RecordMicrophone: false }, "今回の録画準備へ設定が渡らない");
                Check(settings is { ShowPointerGuide: true, RecordSystemAudio: false, RecordMicrophone: true }, "元設定が変更された");
            }
            finally { session.Cancel(); }

            var screenshot = MenuSession();
            try { Check(!screenshot.SelectionMenu.AllCommands.Any(i => i.Name == "RecordingAudio"), "静止画に録画音声項目がある"); }
            finally { screenshot.Cancel(); }
        });
        Run("親側の選択方法・拡大鏡・配信設定をメニューへ接続できる", () =>
        {
            using var menu = new CaptureContextMenu(OutputDestination.Clipboard, _ => { }, () => { }, () => { },
                CaptureMode.FixedRegion);
            bool magnifier = false, share = true, twoClicks = false;
            void UpdateMenu() { menu.SetRectangleSelectionMethod(twoClicks); menu.SetPresentationOptions(magnifier, share); }
            menu.AddRectangleSelectionMethods(() => { twoClicks = false; UpdateMenu(); }, () => { twoClicks = true; UpdateMenu(); });
            menu.AddPresentationOptions(magnifier, value => { magnifier = value; UpdateMenu(); },
                share, value => { share = value; UpdateMenu(); });
            Check(!menu.Command("RectangleSelectionMethod").Available, "固定サイズに選択方法が表示された");
            menu.SetMode(CaptureMode.Rectangle);
            Check(menu.Command("RectangleSelectionMethod").Available && menu.Command("SelectionMethodDrag").Checked, "矩形の初期選択方法が未反映");
            menu.Command("SelectionMethodTwoClicks").PerformClick();
            menu.Command("ShowMagnifier").PerformClick();
            menu.Command("ShareCapturePresentation").PerformClick();
            Check(twoClicks && magnifier && !share && menu.Command("SelectionMethodTwoClicks").Checked
                && menu.Command("ShowMagnifier").Checked && !menu.Command("ShareCapturePresentation").Checked, "親のコールバックとメニューの状態が不一致");
        });
        Run("トレイで選んだモニターは番号が変わっても同じ表示先を解決する", () =>
        {
            var selected = new DisplayTarget("DISPLAY2", new(0, 0, 800, 600), true);
            var moved = selected with { Bounds = new(-1200, 0, 1200, 900), IsPrimary = false };
            var other = new DisplayTarget("DISPLAY3", new(0, 0, 1024, 768), true);
            Equal(moved, InstantCapture.ResolveDisplay(selected, [other, moved]));
            try
            {
                InstantCapture.ResolveDisplay(selected, [other]);
                throw new Exception("切断済みのモニターから別のモニターに撮影を振り替えた");
            }
            catch (InvalidOperationException) { }
        });
        Run("画面固定とカーソルは両方向で再取得し固定枠を保持", () =>
        {
            var calls = new List<(bool, bool)>();
            var session = MenuSession((cursor, observe) => { calls.Add((cursor, observe)); return MenuFrame(cursor ? Color.Blue : Color.Green); });
            try
            {
                session.MoveTo(new(100, 100)); session.HandleCommandKey(Keys.Right); var bounds = session.Selection;
                foreach (var state in new[] { (false, false), (true, true), (true, false), (false, true), (true, true) })
                {
                    WaitHistory(session.ChangeCaptureSourceAsync(state.Item1, state.Item2));
                    Equal(state.Item1, session.OutputSettings.FreezeFrame); Equal(state.Item2, session.OutputSettings.IncludeCursor);
                    Equal(bounds, session.Selection); Check(!session.Refreshing && !session.Completion.IsCompleted, "再取得で選択終了した");
                    Equal((state.Item2 ? Color.Blue : Color.Green).ToArgb(), session.Snapshot.Image.GetPixel(0, 0).ToArgb());
                }
                Equal(5, calls.Count); Check(calls.All(c => !c.Item2), "記録OFFでUIA準備した");
                session.HandleCommandKey(Keys.Enter); WaitHistory(session.Completion); using var result = session.Completion.GetAwaiter().GetResult();
                Equal(5, calls.Count); Equal(Color.Blue.ToArgb(), result.Image!.GetPixel(0, 0).ToArgb());
                Check(result.OutputSettings!.IncludeCursor, "出力へカーソル設定が渡らない");
            }
            finally { session.Cancel(); }
        });
        Run("固定OFFは再取得プレビューではなく確定時の画像を出力", () =>
        {
            int count = 0; var session = MenuSession((_, _) => MenuFrame(++count == 1 ? Color.Green : Color.Blue));
            try
            {
                session.MoveTo(new(100, 100)); WaitHistory(session.ChangeCaptureSourceAsync(false, false));
                session.HandleCommandKey(Keys.Enter); WaitHistory(session.Completion); using var result = session.Completion.GetAwaiter().GetResult();
                Equal(2, count); Equal(Color.Blue.ToArgb(), result.Image!.GetPixel(0, 0).ToArgb());
            }
            finally { session.Cancel(); }
        });
        Run("再取得失敗と画面構成変更は旧設定・旧画像へ復帰", () =>
        {
            foreach (bool layout in new[] { false, true })
            {
                var session = MenuSession((_, _) => layout ? MenuFrame(Color.Blue, 800) : throw new IOException("取得失敗"));
                try
                {
                    WaitHistory(session.ChangeCaptureSourceAsync(false, true));
                    Check(session.OutputSettings.FreezeFrame && !session.OutputSettings.IncludeCursor && !session.Refreshing, "失敗した設定を採用した");
                    Equal(Color.Red.ToArgb(), session.Snapshot.Image.GetPixel(0, 0).ToArgb());
                    Check(session.SelectionError is not null && !session.Completion.IsCompleted, "エラー復帰しない");
                    Check(session.SelectionMenu.Command("FreezeFrame").Checked && !session.SelectionMenu.Command("IncludeCursor").Checked, "失敗後のチェック不一致");
                }
                finally { session.Cancel(); }
            }
        });
        Run("撮影情報を途中で有効にすると記録準備付きで再取得", () =>
        {
            int count = 0; bool observed = false;
            var session = MenuSession((_, observe) => { count++; observed = observe; return MenuFrame(Color.Green); });
            try
            {
                WaitHistory(session.ChangeMetadataAsync(CaptureMetadataMode.Both));
                Check(observed && count == 1, "記録準備せず有効にした");
                Equal(CaptureMetadataMode.Both, session.OutputSettings.MetadataMode);
                WaitHistory(session.ChangeMetadataAsync(CaptureMetadataMode.Json)); WaitHistory(session.ChangeMetadataAsync(CaptureMetadataMode.None));
                Equal(1, count);
                session.SelectionMenu.Command("DestinationFile").PerformClick(); Check(!session.SelectionMenu.Command("MetadataModeMenu").Enabled, "ファイルOFFで情報メニュー有効");
            }
            finally { session.Cancel(); }
        });
        Run("録画メニューは静止画専用項目を除きカーソル設定を出力へ渡す", () =>
        {
            var session = MenuSession((_, observe) => { Check(!observe, "録画でUIA取得した"); return MenuFrame(Color.Green); }, CaptureIntent.Recording);
            try
            {
                Check(!session.SelectionMenu.AllCommands.Any(i => i.Name is "MetadataModeMenu" or "ScreenshotColorDepthMenu" or "ScreenshotPaddingMenu"), "録画に静止画専用項目がある");
                WaitHistory(session.ChangeCaptureSourceAsync(true, true)); session.MoveTo(new(100, 100)); session.HandleCommandKey(Keys.Enter);
                WaitHistory(session.Completion); using var outcome = session.Completion.GetAwaiter().GetResult(); Check(outcome.OutputSettings!.IncludeCursor, "録画へカーソル設定が渡らない");
            }
            finally { session.Cancel(); }
        });
        Run("モニター制限は撮影中だけ切替しチェックを同期", () =>
        {
            var session = MonitorSession(true);
            try
            {
                session.ChangeMonitorLimit(false); Equal(2, session.SelectionMonitors.Count);
                Check(!session.SelectionMenu.Command("LimitToStartingMonitor").Checked, "制限OFFが表示されない");
                session.MoveTo(new(100, 100)); session.ChangeMonitorLimit(true);
                Equal(SelectionTestMonitors[1], session.SelectionMonitors.Single());
                session.UpdateMonitorRestriction(new(-20, 30), true);
                Check(!session.SelectionMenu.Command("LimitToStartingMonitor").Checked, "CtrlShift解除が表示されない");
            }
            finally { session.Cancel(); }
        });
        Run("入力プリセットも今回解除したモニター制限を再有効化しない", () =>
        {
            var displays = SelectionTestMonitors.Select((m, i) => new DisplayLayout("m" + i, "画面" + i, m, 96, 96)).ToArray();
            var desktop = SelectionTestMonitors.Aggregate(Rectangle.Union);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Recording, OutputDestination.None),
                new(new Bitmap(desktop.Width, desktop.Height), desktop, SelectionTestMonitors), [], initialPointer: new(-100, 20), readPresetDisplays: () => displays);
            try
            {
                session.ChangeMonitorLimit(false);
                session.ApplyInputPreset(RegionCoordinates.FromBounds("右の枠", new(20, 20, 100, 80), displays));
                Equal(2, session.SelectionMonitors.Count);
                Check(!session.SelectionMenu.Command("LimitToStartingMonitor").Checked, "入力プリセットが制限を復活させた");
            }
            finally { session.Cancel(); }
        });
        Run("固定座標の入力プリセットで制限先のモニターが変わると表示中のオーバーレイも切り替わる", () =>
        {
            var displays = SelectionTestMonitors.Select((m, i) => new DisplayLayout("m" + i, "画面" + i, m, 96, 96)).ToArray();
            var desktop = SelectionTestMonitors.Aggregate(Rectangle.Union);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Recording, OutputDestination.None,
                    LimitToStartingMonitor: true, ShowSelectionGuide: false),
                new(new Bitmap(desktop.Width, desktop.Height), desktop, SelectionTestMonitors), [], initialPointer: new(-100, 20),
                confineCursor: _ => new MemoryStream(), readPresetDisplays: () => displays);
            try
            {
                session.Start();
                IEnumerable<Rectangle> VisibleOverlays() => Application.OpenForms.OfType<CaptureOverlay>()
                    .Where(overlay => overlay.SelectionMenu == session.SelectionMenu && overlay.Visible).Select(overlay => overlay.MonitorBounds);
                session.ChangeMonitorLimit(false);
                session.MoveTo(new(100, 100));
                session.ChangeMonitorLimit(true);
                Check(VisibleOverlays().SequenceEqual([SelectionTestMonitors[1]]), "制限先のモニターだけを表示していない");
                session.ApplyInputPresetAsync(RegionCoordinates.FromBounds("左の枠", new(-300, 0, 100, 80), displays)).GetAwaiter().GetResult();
                Equal(SelectionTestMonitors[0], session.SelectionMonitors.Single());
                Check(VisibleOverlays().SequenceEqual([SelectionTestMonitors[0]]),
                    "表示中のオーバーレイが制限先と一致しない: " + string.Join(", ", VisibleOverlays()));
            }
            finally { session.Cancel(); }
        });
    }
}
