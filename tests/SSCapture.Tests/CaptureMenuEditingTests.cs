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
        Run("撮影メニューは3分類と設定・キャンセルに整理", () =>
        {
            var session = MenuSession();
            try
            {
                var menu = session.SelectionMenu;
                Equal("CaptureRangeGroup,CaptureOutputGroup,CaptureOperationGroup,OpenSettings,CancelCapture",
                    string.Join(',', menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Name)));
                foreach (string name in new[] { "CaptureModeMenu", "FixedSizeMenu", "FixedAnchorMenu", "InputPresetMenu", "ScreenshotPaddingMenu", "FreezeFrame", "IncludeCursor" })
                    Check(menu.Command("CaptureRangeGroup").DropDownItems.Contains(menu.Command(name)), name + "の分類不一致");
                foreach (string name in new[] { "ScreenshotColorDepthMenu", "MetadataModeMenu", "CapturePresetMenu" })
                    Check(menu.Command("CaptureOutputGroup").DropDownItems.Contains(menu.Command(name)), name + "の分類不一致");
                foreach (string name in new[] { "LimitToStartingMonitor", "DimCaptureBackground", "Grid20", "ShowSelectionGuide" })
                    Check(menu.Command("CaptureOperationGroup").DropDownItems.Contains(menu.Command(name)), name + "の分類不一致");
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
        Run("固定寸法エディタは任意pxの入力と検証", () =>
        {
            Size? applied = null;
            using var editor = new FixedSizeMenuEditor(new(100, 80), s => applied = s);
            using var host = new Form(); host.Controls.Add(editor); _ = host.Handle; _ = editor.Handle; LayoutTree(host);
            var width = (NumericUpDown)editor.Controls.Find("CaptureFixedWidth", true).Single();
            var height = (NumericUpDown)editor.Controls.Find("CaptureFixedHeight", true).Single();
            var apply = (Button)editor.Controls.Find("ApplyCaptureFixedSize", true).Single();
            void Click() => typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(apply, [EventArgs.Empty]);
            width.Value = 321; height.Value = 123; Click(); Equal(new Size(321, 123), applied!.Value);
            width.Value = height.Value = 32768; Click(); Equal(new Size(321, 123), applied!.Value);
        });
        Run("固定寸法の適用は親子ポップアップをすべて閉じて選択を続行", () =>
        {
            var session = MenuSession(); using var host = new Form { ShowInTaskbar = false };
            try
            {
                host.Show(); session.SelectionMenu.Show(host, new Point(20, 20));
                session.SelectionMenu.Command("CaptureRangeGroup").ShowDropDown();
                session.SelectionMenu.Command("FixedSizeMenu").ShowDropDown();
                session.ChangeFixedSize(new(321, 123));
                Check(!session.Completion.IsCompleted, "未開始の確認セッションをキャンセルした");
                Check(!session.SelectionMenu.Visible && session.SelectionMenu.AllCommands.All(i => !i.HasDropDownItems || !i.DropDown.Visible), "子ポップアップが残る");
            }
            finally { session.Cancel(); }
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
        Run("モニタ制限は撮影中だけ切替しチェックを同期", () =>
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
        Run("入力プリセットも今回解除したモニタ制限を再有効化しない", () =>
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
    }
}
