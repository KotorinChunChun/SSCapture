using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static readonly Rectangle[] SelectionTestMonitors = [new(-400, -80, 400, 300), new(0, 0, 640, 480)];

    private static SessionController MonitorSession(bool limited, CaptureMode mode = CaptureMode.Rectangle, Point? start = null)
    {
        Rectangle desktop = SelectionTestMonitors.Aggregate(Rectangle.Union);
        var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, SelectionTestMonitors);
        return new SessionController(new CaptureOptions(mode, CaptureIntent.Recording, OutputDestination.None,
            FixedSize: new Size(100, 100), LimitToStartingMonitor: limited), snapshot, [desktop], initialPointer: start ?? new Point(-100, 20));
    }

    private static void SelectionMonitorTests()
    {
        Run("入力プリセットは登録位置の枠を呼び出し調整後のEnterだけで確定", () =>
        {
            Services.DisplayLayout[] displays = SelectionTestMonitors.Select((m, i) => new Services.DisplayLayout("monitor" + i, "画面" + i, m, 96, 96)).ToArray();
            var region = RegionCoordinates.FromBounds("右の枠", new Rectangle(40, 60, 120, 80), displays);
            Rectangle desktop = SelectionTestMonitors.Aggregate(Rectangle.Union);
            var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, SelectionTestMonitors);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Recording, OutputDestination.None,
                OutputSettings: new AppSettings { SavedRegions = [region] }), snapshot, [], initialPointer: new(-100, 20), readPresetDisplays: () => displays);
            try
            {
                var menu = (ToolStripMenuItem)session.SelectionMenu.Items["InputPresetMenu"]!;
                ((ToolStripMenuItem)menu.DropDownItems[0]).PerformClick();
                Equal(CaptureMode.FixedRegion, session.Mode); Equal(region.Bounds, session.Selection);
                Check(!session.Completion.IsCompleted, "プリセット選択で即撮影した");
                session.MoveTo(new(0, 20)); Equal(region.Bounds, session.Selection);
                session.HandleCommandKey(Keys.Right); Equal(new Rectangle(41, 60, 120, 80), session.Selection);
                session.HandleCommandKey(Keys.Enter);
                using var result = session.Completion.GetAwaiter().GetResult();
                Equal(new Rectangle(41, 60, 120, 80), result.Selection!.Bounds);
            }
            finally { session.Cancel(); }
        });
        Run("入力プリセットの切断エラーは枠と撮影を壊さず表示", () =>
        {
            var session = MonitorSession(true);
            try
            {
                var displays = new[] { new DisplayLayout("missing", "切断画面", new(0, 0, 640, 480), 96, 96) };
                var region = RegionCoordinates.FromBounds("旧画面", new(0, 0, 100, 100), displays);
                session.ApplyInputPreset(region);
                Check(session.SelectionError is not null && !session.Completion.IsCompleted, "切断エラーを失った");
                Equal(CaptureMode.Rectangle, session.Mode);
            }
            finally { session.Cancel(); }
        });
        Run("CtrlShiftの別モニタ移動だけが撮影単位の制限を解除する", () =>
        {
            var session = MonitorSession(true);
            try
            {
                session.UpdateMonitorRestriction(new(20, 30), false); Equal(1, session.SelectionMonitors.Count);
                session.UpdateMonitorRestriction(new(-20, 30), true); Equal(1, session.SelectionMonitors.Count);
                session.UpdateMonitorRestriction(new(20, -50), true); Equal(1, session.SelectionMonitors.Count);
                session.UpdateMonitorRestriction(new(20, 30), true); Equal(2, session.SelectionMonitors.Count);
                session.MoveTo(new(600, 400)); Equal(new Point(600, 400), session.Current);
                session.UpdateMonitorRestriction(new(-20, 30), false); Equal(2, session.SelectionMonitors.Count);
            }
            finally { session.Cancel(); }
            var next = MonitorSession(true); Equal(1, next.SelectionMonitors.Count); next.Cancel();
        });
        Run("モニタ拡張はドラッグの始点と選択を維持する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [SelectionTestMonitors[0]], []);
            gesture.Begin(new(-100, 20), new(4, 4)); gesture.Move(new(-20, 50));
            gesture.ExpandMonitors(SelectionTestMonitors); gesture.Move(new(100, 120));
            Equal(new Rectangle(-100, 20, 200, 100), gesture.Selection);
            Check(gesture.IsPressed && gesture.HasDragged, "ドラッグをリセットした");
        });
        Run("開始画面限定は既定ON・旧設定ON・明示OFFを保存", () => WithServiceDirectory(directory =>
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            Check(new AppSettings().LimitToStartingMonitor, "新規設定の既定がOFF");
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");
            Check(store.Load(out _).LimitToStartingMonitor, "旧設定の既定がOFF");
            foreach (bool limited in new[] { false, true })
            {
                store.Save(new AppSettings { LimitToStartingMonitor = limited });
                Equal(limited, store.Load(out _).LimitToStartingMonitor);
            }
        }));
        Run("開始画面限定の設定は基本タブに収まりON/OFFを保存", () =>
        {
            foreach (bool limited in new[] { false, true })
            {
                AppSettings? saved = null;
                using var form = new SettingsForm(new AppSettings { LimitToStartingMonitor = limited }, s => { saved = s; return null; }, () => { }, () => []);
                var toggle = (CheckBox)form.Controls.Find("LimitToStartingMonitor", true).Single();
                Equal(limited, toggle.Checked);
                _ = form.Handle; foreach (Control child in Descendants(form)) _ = child.Handle; LayoutTree(form);
                Check(toggle.Parent!.ClientRectangle.Contains(toggle.Bounds) && toggle.Height >= toggle.PreferredSize.Height,
                    "開始画面限定チェックが見切れる");
                toggle.Checked = !limited;
                typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
                Check(saved is not null, "設定が保存されない");
                Equal(!limited, saved!.LimitToStartingMonitor);
            }
        });
        Run("限定ONは開始モニタだけを選び画面外のポインタ座標と窓候補を制限", () =>
        {
            var session = MonitorSession(true, CaptureMode.Window);
            try
            {
                Equal(1, session.SelectionMonitors.Count);
                Equal(SelectionTestMonitors[0], session.SelectionMonitors[0]);
                session.MoveTo(new Point(600, 400));
                Equal(new Point(-1, 219), session.Current);
                Equal(SelectionTestMonitors[0], session.Selection);
                Equal(SelectionTestMonitors[0], session.SelectionMonitors[0]);
                session.CompleteSelection(new CaptureSelection(new Rectangle(-10, 0, 20, 20), null, OutputDestination.None));
                Check(!session.Completion.IsCompleted, "境界をまたぐ範囲を確定した");
                session.CompleteSelection(new CaptureSelection(new Rectangle(-10, 0, 10, 20), null, OutputDestination.None));
                using CaptureOutcome result = session.Completion.GetAwaiter().GetResult();
                Equal(new Rectangle(-10, 0, 10, 20), result.Selection!.Bounds);
            }
            finally { session.Cancel(); }
        });
        Run("限定OFFは全モニタを維持し画面間の選択を許可", () =>
        {
            var session = MonitorSession(false);
            try
            {
                Equal(2, session.SelectionMonitors.Count);
                session.MoveTo(new Point(600, 400)); Equal(new Point(600, 400), session.Current);
                session.CompleteSelection(new CaptureSelection(new Rectangle(-10, 0, 20, 20), null, OutputDestination.None));
                using CaptureOutcome result = session.Completion.GetAwaiter().GetResult();
                Equal(new Rectangle(-10, 0, 20, 20), result.Selection!.Bounds);
            }
            finally { session.Cancel(); }
        });
        Run("固定範囲の矢印調整でも開始画面外は確定できない", () =>
        {
            var session = MonitorSession(true, CaptureMode.FixedRegion);
            try
            {
                session.MoveTo(new Point(-100, 20)); Check(session.FixedSelectionValid, "画面内の固定範囲が無効");
                session.HandleCommandKey(Keys.Right); Check(!session.FixedSelectionValid, "画面外の固定範囲が有効");
                session.HandleCommandKey(Keys.Enter); Check(!session.Completion.IsCompleted, "画面外で確定した");
            }
            finally { session.Cancel(); }
        });
        Run("ポインタ制限は元の範囲へ一度だけ復元し復元失敗時は解除を試みる", () =>
        {
            var calls = new List<Rectangle?>();
            Rectangle previous = new(-300, 0, 100, 100);
            var clip = new CursorConfinement(SelectionTestMonitors[0], previous, calls.Add);
            clip.Dispose(); clip.Dispose();
            Equal(2, calls.Count); Equal<Rectangle?>(previous, calls[1]);
            calls.Clear();
            var failure = new CursorConfinement(SelectionTestMonitors[0], previous, r =>
            { calls.Add(r); if (r == previous) throw new InvalidOperationException("復元エラー"); });
            bool rejected = false;
            try { failure.Dispose(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && calls.Count == 3 && calls[2] is null, "復元エラー時に解除されない");
        });
    }

    // 明示指定のみ。単色画面で実ポインタの制限と全終了経路の復元を確認する。
    private static int RunSelectionMonitorSmoke()
    {
        using var input = new NativeSmokeInput();
        Rectangle originalClip = CursorConfinement.ReadBounds();
        try
        {
            var monitors = CaptureNative.MonitorBounds();
            foreach (bool limited in new[] { true, false })
            foreach (Rectangle target in monitors)
            foreach (string finish in new[] { "cancel", "complete", "deactivate", "settings", "error" })
            {
                Point start = new(target.Left + target.Width / 2, target.Top + target.Height / 2);
                input.Move(start);
                Rectangle desktop = monitors.Aggregate(Rectangle.Union);
                var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, monitors);
                using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.DarkSlateBlue);
                var session = new SessionController(new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot,
                    OutputDestination.None, FreezeFrame: finish != "error", LimitToStartingMonitor: limited), snapshot, [],
                    () => throw new InvalidOperationException("検証用の画像取得失敗"), start);
                try
                {
                    session.Start();
                    PumpOverlayMessages(TimeSpan.FromMilliseconds(100));
                    Check(!session.Completion.IsCompleted, "開始時にセッションが終了した");
                    var overlays = Application.OpenForms.OfType<CaptureOverlay>().ToArray();
                    Equal(limited ? 1 : monitors.Count, overlays.Length);
                    foreach (CaptureOverlay overlay in overlays) AssertOverlayPlacement(overlay);
                    Equal(limited ? target : originalClip, CursorConfinement.ReadBounds());
                    if (limited)
                    {
                        Equal(target, overlays.Single().MonitorBounds);
                        foreach (Point outside in new[] { new Point(target.Left - 20, start.Y), new Point(target.Right + 20, start.Y),
                            new Point(start.X, target.Top - 20), new Point(start.X, target.Bottom + 20) })
                        {
                            input.Move(outside);
                            Check(target.Contains(CaptureNative.CursorPosition()), "ポインタが開始画面から出た");
                        }
                    }
                    switch (finish)
                    {
                        case "cancel": session.HandleCommandKey(Keys.Escape); break;
                        case "deactivate": session.ApplicationDeactivated(); break;
                        case "settings": session.SelectionMenu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "設定").PerformClick(); break;
                        default: session.CompleteSelection(new CaptureSelection(new Rectangle(start, new Size(20, 20)), null, OutputDestination.None)); break;
                    }
                    if (finish == "error") Check(session.Completion.IsFaulted, "画像取得失敗が通知されない");
                    else { using CaptureOutcome result = session.Completion.GetAwaiter().GetResult(); }
                    Equal(originalClip, CursorConfinement.ReadBounds());
                    Equal(0, Application.OpenForms.OfType<CaptureOverlay>().Count());
                }
                finally { session.Cancel(); }
            }
            Console.WriteLine($"PASS 開始画面限定ON/OFF・{monitors.Count}モニタ・5終了経路・ポインタ4辺の制限と復元");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL 開始画面限定: {ex}"); return 1; }
    }
}
