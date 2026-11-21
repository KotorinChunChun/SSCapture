using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunSelectionInputSmoke()
    {
        int successes = 0, failures = 0;
        bool inputUnavailable = false;
        using var host = new HitFixture(false) { Text = "選択操作の検証", Bounds = new(100, 100, 800, 540), BackColor = Color.White };
        host.Shown += async (_, _) =>
        {
            using var input = new NativeSmokeInput();
            try
            {
                NativeSmokeInput.FocusFixture(host.Handle);
                await Task.Delay(150);
                Rectangle bounds = host.RectangleToScreen(host.ClientRectangle);
                Point P(int x, int y) => new(bounds.Left + x, bounds.Top + y);
                async Task Move(CaptureOverlay overlay, int x, int y)
                {
                    NativeSmokeInput.RequireForeground(overlay.Handle);
                    input.Move(P(x, y)); await Task.Delay(70);
                }
                async Task Down(CaptureOverlay overlay, int x = 80, int y = 80)
                {
                    await Move(overlay, x, y); NativeSmokeInput.RequireTarget(overlay.Handle, P(x, y));
                    input.LeftDown(); await Task.Delay(70);
                }
                async Task Key(CaptureOverlay overlay, byte key, bool down)
                {
                    NativeSmokeInput.RequireForeground(overlay.Handle);
                    if (down) input.KeyDown(key); else input.KeyUp(key);
                    await Task.Delay(70);
                }
                async Task MenuClick(CaptureOverlay overlay, ToolStripMenuItem item)
                {
                    var menu = RevealMenuCommand(overlay.SelectionMenu, item);
                    Point point = menu.PointToScreen(new(item.Bounds.Left + item.Bounds.Width / 2, item.Bounds.Top + item.Bounds.Height / 2));
                    input.Move(point); await Task.Delay(70);
                    NativeSmokeInput.RequireMenuTarget(overlay.Handle, menu.Handle, point);
                    input.LeftDown(); await Task.Delay(50); input.LeftUp(); await Task.Delay(120);
                }
                async Task Test(string name, Func<CancellationToken, Task> run)
                {
                    if (inputUnavailable) return;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        NativeSmokeInput.FocusFixture(host.Handle);
                        await Task.Delay(120, timeout.Token);
                        await run(timeout.Token); AssertNoCaptureOverlays(); successes++;
                        Console.WriteLine("PASS INPUT " + name);
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Console.WriteLine("FAIL INPUT " + name + ": " + ex.Message);
                        inputUnavailable = ex.Message.Contains("最前面", StringComparison.Ordinal);
                    }
                    finally { input.ReleaseInjectedInput(); CloseSmokeOverlays(); }
                }
                await Test("Shift/Ctrlの方向固定を離したらポインタと選択端が一致", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        var session = OverlaySession(overlay);
                        await Down(overlay); await Move(overlay, 180, 140);
                        await Key(overlay, 0xA4, true); await Key(overlay, 0xA0, true); await Move(overlay, 230, 200);
                        Equal(new Size(150, 60), session.Selection.Size);
                        await Key(overlay, 0xA0, false);
                        Equal(new Size(150, 120), session.Selection.Size);
                        await Key(overlay, 0xA2, true); await Move(overlay, 260, 220);
                        Equal(new Size(150, 140), session.Selection.Size);
                        await Key(overlay, 0xA2, false);
                        await Key(overlay, 0xA4, false);
                        Equal(new Size(180, 140), session.Selection.Size);
                        await Move(overlay, 270, 230); Equal(new Size(190, 150), session.Selection.Size);
                        input.LeftUp();
                    }, token, showGuide: false, settings: new() { CaptureKeys = CaptureKeyDefaults.Current.Drag with { Alt = CaptureKeyAction.None } });
                    Equal(new Size(190, 150), result.Image!.Size);
                });
                foreach (bool escape in new[] { false, true })
                    await Test((escape ? "Esc" : "右クリック") + "は選択だけ中断し再ドラッグ可能", async token =>
                    {
                        using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                        {
                            await Down(overlay); await Move(overlay, 220, 160);
                            if (escape) { await Key(overlay, 0x1B, true); await Key(overlay, 0x1B, false); }
                            else { input.RightDown(); await Task.Delay(60); input.RightUp(); }
                            input.LeftUp(); await Task.Delay(120);
                            Check(!overlay.IsDisposed && !overlay.SelectionMenu.Visible, "撮影終了またはメニュー表示");
                            Check(!OverlaySession(overlay).Dragging && OverlaySession(overlay).Selection.IsEmpty, "選択が残る");
                            await Down(overlay, 100, 80); await Move(overlay, 220, 160); input.LeftUp();
                        }, token, showGuide: false);
                        Equal(new Size(120, 80), result.Image!.Size);
                    });
                foreach (bool middle in new[] { false, true })
                foreach (bool escape in new[] { false, true })
                    await Test((middle ? "中押下" : "左単押下") + (escape ? "をEscで取り消す" : "を右クリックで取り消す"), async token =>
                    {
                        using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, _) =>
                        {
                            var session = OverlaySession(overlay);
                            if (middle)
                            {
                                await Move(overlay, 80, 80);
                                NativeSmokeInput.RequireTarget(overlay.Handle, P(80, 80));
                                input.MiddleDown(); await Task.Delay(100);
                            }
                            else await Down(overlay);
                            string expected = middle ? "モニター全体を撮影" : "ウィンドウを撮影";
                            Equal(expected + "　ESC / 右クリックで取り消し" + (middle ? "\nドラッグ：複数デスクトップを合成" : ""), session.PointerHint);
                            if (escape) { await Key(overlay, 0x1B, true); await Key(overlay, 0x1B, false); }
                            else { input.RightDown(); await Task.Delay(60); input.RightUp(); }
                            if (middle) input.MiddleUp(); else input.LeftUp();
                            await Task.Delay(100);
                            Check(session.PointerHint is null && !session.HasPointerPress && !session.Completion.IsCompleted,
                                "取り消し後も表示が残るか、取り消した撮影が出力された");
                            await Down(overlay); await Move(overlay, 200, 160); input.LeftUp();
                        }, token, showGuide: false);
                        Equal(new Size(120, 80), result.Image!.Size);
                    });
                await Test("割り当てたグリッドを押している間だけ使用し、メニューでは常時使用する", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        var session = OverlaySession(overlay);
                        await Down(overlay); await Move(overlay, 153, 137);
                        Equal(new Size(73, 57), session.Selection.Size);
                        await Key(overlay, 0xA4, true); Equal(new Size(80, 60), session.Selection.Size);
                        await Key(overlay, 0xA4, false); Equal(new Size(73, 57), session.Selection.Size);
                        await Key(overlay, 0x1B, true); await Key(overlay, 0x1B, false); input.LeftUp(); await Task.Delay(100);
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        await MenuClick(overlay, (ToolStripMenuItem)overlay.SelectionMenu.Command("Grid20")!);
                        Check(session.Grid20, "メニュー20pxが反映されない");
                        await Down(overlay); await Move(overlay, 153, 137); input.LeftUp();
                    }, token, settings: new() { GridStepPixels = 20, CaptureKeys = new(Alt: CaptureKeyAction.Grid) }, showGuide: false);
                    Equal(new Size(80, 60), result.Image!.Size);
                });
                await Test("撮影範囲設定前のAlt単押しで枠スナップを切り替える", async token =>
                {
                    Rectangle target = new(P(100, 100), new Size(120, 120));
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, _) =>
                    {
                        var session = OverlaySession(overlay);
                        await Move(overlay, 80, 80);
                        await Key(overlay, 0xA4, true); await Key(overlay, 0xA4, false);
                        Equal("ウィンドウ枠スナップ ON", session.ModifierHint);
                        await Down(overlay); await Move(overlay, 108, 109);
                        Equal(P(100, 100), session.Current);
                        await Key(overlay, 0x1B, true); await Key(overlay, 0x1B, false); input.LeftUp(); await Task.Delay(100);
                        await Move(overlay, 80, 80);
                        await Key(overlay, 0xA4, true); await Key(overlay, 0xA4, false);
                        Check(session.ModifierHint is null, "再度のAlt単押しで解除できない");
                        await Down(overlay); await Move(overlay, 108, 109); input.LeftUp();
                    }, token, windows: [target], showGuide: false);
                    Equal(new Size(28, 29), result.Image!.Size);
                });
                await Test("Spaceで実ポインタを対角へ移動して同じ範囲を確定", async token =>
                {
                    Rectangle expected = Rectangle.FromLTRB(P(80, 80).X, P(80, 80).Y, P(230, 180).X, P(230, 180).Y);
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, _) =>
                    {
                        await Down(overlay); await Move(overlay, 230, 180);
                        Equal(expected, OverlaySession(overlay).Selection);
                        await Key(overlay, 0x20, true); await Key(overlay, 0x20, false);
                        Equal(P(80, 80), CaptureNative.CursorPosition());
                        Equal(expected, OverlaySession(overlay).Selection);
                        input.LeftUp();
                    }, token, captureWindowsKey: true, showGuide: false);
                    Equal(expected, result.Selection!.Bounds);
                });
                await Test("カーソル切替で再取得に失敗しても次のドラッグで撮影", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        var item = (ToolStripMenuItem)overlay.SelectionMenu.Command("IncludeCursor")!;
                        await MenuClick(overlay, item);
                        Check(!overlay.SelectionMenu.Visible, "再取得失敗後にメニューが残る");
                        var session = OverlaySession(overlay);
                        Check(session.SelectionError?.Contains("試験用の再取得失敗", StringComparison.Ordinal) == true,
                            "再取得の失敗理由が表示されない");
                        Check(!session.OutputSettings.IncludeCursor, "失敗した切替が適用された");
                        await Down(overlay); await Move(overlay, 240, 180); input.LeftUp();
                    }, token, currentFrameFactory: () => throw new IOException("試験用の再取得失敗"), showGuide: false);
                    Equal(new Size(160, 100), result.Image!.Size);
                });
                await Test("一時モード選択はセッション内で変更し画像を維持", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        var root = (ToolStripMenuItem)overlay.SelectionMenu.Command("CaptureModeMenu")!;
                        var rootOwner = RevealMenuCommand(overlay.SelectionMenu, root);
                        Point rootPoint = rootOwner.PointToScreen(new(root.Bounds.Left + 30, root.Bounds.Top + root.Bounds.Height / 2));
                        input.Move(rootPoint); await Task.Delay(450);
                        if (!root.DropDown.Visible) root.ShowDropDown();
                        await Task.Delay(80);
                        // Native input to the submenu, after validating its actual HWND.
                        var item = root.DropDownItems.Cast<ToolStripMenuItem>().Single(i => (CaptureMode)i.Tag! == CaptureMode.Window);
                        Point point = root.DropDown.PointToScreen(new(item.Bounds.Left + item.Bounds.Width / 2, item.Bounds.Top + item.Bounds.Height / 2));
                        Check(root.DropDown.GetItemAt(root.DropDown.PointToClient(point)) == item, "サブメニューのクリック座標不一致");
                        input.Move(point); await Task.Delay(60);
                        NativeSmokeInput.RequireMenuTarget(overlay.Handle, root.DropDown.Handle, point);
                        input.LeftDown(); await Task.Delay(50); input.LeftUp(); await Task.Delay(120);
                        Check(OverlaySession(overlay).Mode == CaptureMode.Window,
                            $"mode={OverlaySession(overlay).Mode}, disposed={overlay.IsDisposed}, completion={OverlaySession(overlay).Completion.Status}, submenu={root.DropDown.Visible}");
                        Check(!overlay.SelectionMenu.Visible && !root.DropDown.Visible, "モード確定後に親/子メニューが残る");
                        NativeSmokeInput.RequireForeground(overlay.Handle);
                        await Down(overlay, 120, 80); input.LeftUp();
                    }, token, showGuide: false);
                    Equal(bounds, result.Selection!.Bounds);
                });
                await Test("中ボタンを押している間は明るいモニタ全体を撮影する", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input, async (overlay, ct) =>
                    {
                        await Move(overlay, 120, 80); NativeSmokeInput.RequireTarget(overlay.Handle, P(120, 80));
                        Check(FeedbackNative.GetWindowDisplayAffinity(overlay.Handle, out uint affinity) && affinity == CaptureNative.ExcludeFromCapture,
                            "選択画面の録画除外が設定されていない");
                        using var rendered = new Bitmap(bounds.Width, bounds.Height);
                        overlay.DrawToBitmap(rendered, new(Point.Empty, bounds.Size));
                        Check(rendered.GetPixel(260, 30).B < 200, "押下前の背景が暗転していない");
                        input.MiddleDown(); await Task.Delay(60);
                        Equal(bounds, OverlaySession(overlay).HighlightedWindow);
                        Equal(Rectangle.Empty, OverlaySession(overlay).DisplaySelection);
                        overlay.DrawToBitmap(rendered, new(Point.Empty, bounds.Size));
                        Equal(Color.Blue.ToArgb(), rendered.GetPixel(260, 30).ToArgb());
                        input.MiddleUp();
                    }, token, fixedSize: new(200, 120), showGuide: false, dimBackground: true);
                    Equal(bounds, result.Selection!.Bounds);
                });
                await Test("メニューのデスクトップ全体はメニュー開始位置を使用", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input, async (overlay, ct) =>
                    {
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        await MenuClick(overlay, (ToolStripMenuItem)overlay.SelectionMenu.Command("CaptureDesktop")!);
                    }, token, fixedSize: new(200, 120), showGuide: false);
                    Equal(bounds, result.Selection!.Bounds);
                });
                await Test("開始モニター限定でも中ドラッグを解除し、往復で通過したモニターを合成する", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, _) =>
                    {
                        await Move(overlay, 100, 110);
                        NativeSmokeInput.RequireTarget(overlay.Handle, P(100, 110));
                        input.MiddleDown(); await Task.Delay(70);
                        var session = OverlaySession(overlay);
                        Check(session.PointerHint?.Contains("ドラッグ：複数デスクトップを合成", StringComparison.Ordinal) == true, "中ドラッグの案内がない");
                        await Move(overlay, 560, 110);
                        Equal(P(560, 110), session.Pointer);
                        CaptureOverlay second = Application.OpenForms.OfType<CaptureOverlay>().Single(form => form.MonitorBounds.Contains(P(560, 110)));
                        Check(second.Visible && session.HighlightForOverlay(second.MonitorBounds) == second.MonitorBounds, "通過したモニターが明るくならない");
                        Check(overlay.Capture && CaptureNative.OwnsForegroundWindow(), "別モニターへ移動すると中ボタンを保持できない");
                        input.Move(P(100, 110)); await Task.Delay(70);
                        input.MiddleUp();
                    }, token, splitMonitors: true, limitToStart: true, showGuide: false, dimBackground: true);
                    Equal(bounds, result.Selection!.Bounds);
                    Equal(2, result.Selection.Composite!.Regions!.Count);
                    Equal(Color.Red.ToArgb(), result.Image!.GetPixel(50, 50).ToArgb());
                    Equal(Color.Blue.ToArgb(), result.Image.GetPixel(560, 50).ToArgb());
                });
                await Test("通常の左単押しは対象を強調して見えている画面を切り抜く", async token =>
                {
                    NativeSmokeInput.FocusFixture(host.Handle);
                    CaptureNative.SetWindowPos(host.Handle, new(-1), host.Left, host.Top, host.Width, host.Height, 0x40);
                    await Task.Delay(100, token);
                    var windows = CaptureNative.CaptureWindows(bounds);
                    var target = windows.First(window => window.Bounds.Contains(P(120, 80)));
                    Equal(bounds, target.Bounds);
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        await Down(overlay, 120, 80);
                        Equal(bounds, OverlaySession(overlay).HighlightedWindow);
                        input.LeftUp();
                    }, token, windows: windows.Select(window => window.Bounds).ToArray(),
                        findWindow: point => windows.FirstOrDefault(window => window.Bounds.Contains(point)), showGuide: false);
                    Equal(bounds, result.Selection!.Bounds);
                    Check(result.Selection.IsolatedWindow is null, "通常の左単押しでWGCを使用した");
                    Equal(Color.Blue.ToArgb(), result.Image!.GetPixel(250, 100).ToArgb());
                });
                foreach (bool viaWin in new[] { false, true })
                await Test((viaWin ? "Win短押し" : "右クリックメニュー") + "でWGCプレビューと保存PNGの画素が一致", async token =>
                {
                    CaptureWindow target = CaptureNative.CaptureWindows(Screen.FromControl(host).Bounds)
                        .Single(window => window.Handle == host.Handle);
                    Color previewColor = host.BackColor;
                    using var result = await CaptureSyntheticOutcome(viaWin ? CaptureMode.Rectangle : CaptureMode.Window, bounds, input, async (overlay, ct) =>
                    {
                        var item = (ToolStripMenuItem)overlay.SelectionMenu.Command("IsolatedWindowCapture")!;
                        if (viaWin)
                        {
                            await Move(overlay, 120, 80);
                            OverlaySession(overlay).ApplyPointerInput(P(120, 80), CaptureModifiers.None, windowsTap: true);
                        }
                        else
                        {
                            await OpenSelectionMenu(overlay, bounds, input, ct);
                            await MenuClick(overlay, item);
                        }
                        Check(item.Checked, "単ウィンドウ撮影の方式がWGCになっていない");
                        Check(!overlay.SelectionMenu.Visible, "WGC選択後にメニューが残る");
                        await Move(overlay, 120, 80);
                        var session = OverlaySession(overlay);
                        for (int i = 0; i < 120 && session.PreviewFrame is null; i++) await Task.Delay(25, ct);
                        Check(session.PreviewFrame is not null, "WGCプレビューが表示されない: " + session.PointerHint);
                        Equal(target.Bounds, session.PreviewFrame!.Bounds);
                        using var rendered = new Bitmap(bounds.Width, bounds.Height);
                        overlay.DrawToBitmap(rendered, new(Point.Empty, bounds.Size));
                        Equal(previewColor.ToArgb(), rendered.GetPixel(bounds.Width / 2, bounds.Height / 2).ToArgb());
                        // The saved image must still match the preview even after the real window redraws.
                        host.BackColor = Color.Lime;
                        host.Refresh();
                        await Down(overlay, 120, 80); input.LeftUp();
                    }, token, windows: [target.Bounds], findWindow: point => target.Bounds.Contains(point) ? target : null, showGuide: false);
                    Equal(target, result.Selection!.IsolatedWindow);
                    Equal(target.Title, result.Selection.WindowTitle);
                    string directory = Path.Combine(Path.GetTempPath(), "sscapture-wgc-" + Guid.NewGuid().ToString("N"));
                    string? savedPath = null;
                    try
                    {
                        using var router = new OutputRouter(directory, delivered: delivery =>
                        {
                            savedPath = delivery.SavedPath;
                            delivery.NotificationImage?.Dispose();
                        });
                        var output = new AppSettings { SaveDirectory = directory, DefaultDestination = OutputDestination.File };
                        await InstantCapture.DeliverAsync(result.Image!, output, DateTimeOffset.Now, router, preserveOutputs: true);
                        Check(savedPath is not null, "WGCのPNGが保存されない");
                        using var saved = new Bitmap(savedPath!);
                        Equal(result.Image!.Size, saved.Size);
                        Equal(previewColor.ToArgb(), saved.GetPixel(saved.Width / 2, saved.Height / 2).ToArgb());
                    }
                    finally
                    {
                        if (savedPath is not null) File.Delete(savedPath);
                        host.BackColor = previewColor;
                        host.Refresh();
                    }
                });
                foreach (bool freeze in new[] { true, false })
                    await Test("実入力後の撮影タイミング " + (freeze ? "ON" : "OFF"), async token =>
                    {
                        int reads = 0;
                        DesktopSnapshot Future()
                        {
                            Check(!Application.OpenForms.OfType<CaptureOverlay>().Any(), "再取得前に選択画面が残る");
                            reads++;
                            Bitmap bitmap = new(bounds.Width, bounds.Height);
                            using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(Color.Green);
                            return new(bitmap, bounds, [bounds]);
                        }
                        using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                        {
                            await Down(overlay); await Move(overlay, 180, 160); input.LeftUp();
                        }, token, freeze: freeze, currentFrameFactory: Future, showGuide: false);
                        Equal(freeze ? 0 : 1, reads);
                        Equal((freeze ? Color.Red : Color.Green).ToArgb(), result.Image!.GetPixel(10, 10).ToArgb());
                    });
                foreach (bool owned in new[] { true, false })
                await Test(owned ? "WGC左単押しは前面の所有ウィンドウをプレビューと撮影に含める" : "WGC左ドラッグは枠なしで軌跡を描き、通過したウィンドウを合成する", async token =>
                {
                    using var independent = owned ? null : new HitFixture(false) { Text = "最初に選ぶ検証用ウィンドウ", Bounds = new(P(55, 155), new Size(240, 155)), BackColor = Color.White };
                    independent?.Show();
                    using var second = new HitFixture(false) { Text = "合成する検証用ウィンドウ", Bounds = new(P(380, 180), new Size(250, 160)), BackColor = Color.Orange };
                    if (owned) second.Show(host); else second.Show();
                    second.Refresh();
                    await Task.Delay(125, token);
                    nint firstHandle = independent?.Handle ?? host.Handle;
                    CaptureWindow[] targets = CaptureNative.CaptureWindows(Screen.FromControl(host).Bounds)
                        .Where(w => w.Handle == firstHandle || w.Handle == second.Handle).ToArray();
                    Equal(2, targets.Length);
                    CaptureWindow parent = targets.Single(window => window.Handle == firstHandle);
                    var child = targets.Single(window => window.Handle == second.Handle);
                    Equal(owned, child.Owner == parent.Handle);
                    Check(child.ZOrder < parent.ZOrder, "検証ウィンドウが前面にない");
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        await Move(overlay, 80, 220);
                        var session = OverlaySession(overlay);
                        session.ApplyPointerInput(P(80, 220), CaptureModifiers.None, windowsTap: true);
                        if (owned)
                        {
                            for (int count = 0; count < 120 && session.PreviewFrame is null; count++) await Task.Delay(25, ct);
                            Check(session.PreviewFrame is not null, "所有ウィンドウを含むプレビューが取得できない: " + session.PointerHint);
                            Equal(Color.Orange.ToArgb(), session.PreviewFrame!.Image.GetPixel(440, 220).ToArgb());
                        }
                        await Down(overlay, 80, 220);
                        Check(session.PointerHint?.Contains("ドラッグ：複数ウィンドウを合成", StringComparison.Ordinal) == true, "左ドラッグの案内がない");
                        if (!owned)
                        {
                            await Move(overlay, 450, 220);
                            Equal(Rectangle.Empty, session.DisplaySelection);
                            Check(session.WindowDragPath.Count >= 2, "軌跡が記録されていない");
                            using var rendered = new Bitmap(bounds.Width, bounds.Height);
                            overlay.DrawToBitmap(rendered, new(Point.Empty, bounds.Size));
                            Equal(Color.FromArgb(55, 195, 255).ToArgb(), rendered.GetPixel(250, 220).ToArgb());
                            Equal(Color.Blue.ToArgb(), rendered.GetPixel(250, 200).ToArgb());
                            Equal(Color.Blue.ToArgb(), rendered.GetPixel(440, 200).ToArgb());
                            Check(rendered.GetPixel(330, 200).B < 200, "選択されていない領域まで暗転が解除された");
                            CaptureNative.SynchronizeDesktop();
                            using var screen = InstantCapture.Take(new Rectangle(P(250, 200), new Size(2, 2)));
                            Equal(Color.White.ToArgb(), screen.GetPixel(0, 0).ToArgb());
                        }
                        input.LeftUp();
                    }, token, windows: targets.Select(window => window.Bounds).ToArray(), showGuide: false, dimBackground: !owned,
                        findWindow: point => targets.FirstOrDefault(window => window.Bounds.Contains(point)),
                        findWindowGroup: window => CaptureWindowGroups.ForegroundGroup(window, targets));
                    Rectangle resultBounds = result.Selection!.Bounds;
                    int PixelAt(int x, int y) => result.Image!.GetPixel(P(x, y).X - resultBounds.X, P(x, y).Y - resultBounds.Y).ToArgb();
                    Equal(Color.White.ToArgb(), PixelAt(180, 200));
                    Equal(Color.Orange.ToArgb(), PixelAt(440, 220));
                    if (!owned) Equal(2, result.Selection!.Composite!.WindowRoots!.Count);
                });
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL INPUT setup: " + ex.Message); }
            finally { input.ReleaseInjectedInput(); CloseSmokeOverlays(); host.Close(); }
        };
        Application.Run(host);
        Console.WriteLine($"SELECTION INPUT RESULT: {successes} passed, {failures} failed");
        return failures == 0 && successes == 22 ? 0 : 1;
    }

    private static SessionController OverlaySession(CaptureOverlay overlay) => (SessionController)typeof(CaptureOverlay)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(overlay)!;
}
