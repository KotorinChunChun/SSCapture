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
                        await Key(overlay, 0xA0, true); await Move(overlay, 230, 200);
                        Equal(new Size(150, 60), session.Selection.Size);
                        await Key(overlay, 0xA0, false);
                        Equal(new Size(150, 120), session.Selection.Size);
                        await Key(overlay, 0xA2, true); await Move(overlay, 260, 220);
                        Equal(new Size(150, 140), session.Selection.Size);
                        await Key(overlay, 0xA2, false);
                        Equal(new Size(180, 140), session.Selection.Size);
                        await Move(overlay, 270, 230); Equal(new Size(190, 150), session.Selection.Size);
                        input.LeftUp();
                    }, token, showGuide: false);
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
                await Test("Alt一時20pxとメニュー20px・保持", async token =>
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
                    }, token, settings: new() { GridStepPixels = 20 }, showGuide: false);
                    Equal(new Size(80, 60), result.Image!.Size);
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
                await Test("固定サイズ中央クリックでモニタ全体", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input, async (overlay, ct) =>
                    {
                        await Move(overlay, 120, 80); NativeSmokeInput.RequireTarget(overlay.Handle, P(120, 80));
                        input.MiddleDown(); await Task.Delay(60); input.MiddleUp();
                    }, token, fixedSize: new(200, 120), showGuide: false);
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
                await Test("native列挙の対象ウィンドウを左単押しで確定", async token =>
                {
                    NativeSmokeInput.FocusFixture(host.Handle);
                    CaptureNative.SetWindowPos(host.Handle, new(-1), host.Left, host.Top, host.Width, host.Height, 0x40);
                    await Task.Delay(100, token);
                    var windows = CaptureNative.WindowBounds(bounds);
                    Equal(bounds, windows.First(r => r.Contains(P(120, 80))));
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        await Down(overlay, 120, 80); input.LeftUp();
                    }, token, windows: windows, showGuide: false);
                    Equal(bounds, result.Selection!.Bounds);
                });
                await Test("WGCを実メニューで選択して対象の画素をPNGへ保存", async token =>
                {
                    CaptureWindow target = CaptureNative.CaptureWindows(Screen.FromControl(host).Bounds)
                        .Single(window => window.Handle == host.Handle);
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Window, bounds, input, async (overlay, ct) =>
                    {
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        var item = (ToolStripMenuItem)overlay.SelectionMenu.Command("IsolatedWindowCapture")!;
                        await MenuClick(overlay, item);
                        Check(!overlay.SelectionMenu.Visible, "WGC選択後にメニューが残る");
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
                        Equal(host.BackColor.ToArgb(), saved.GetPixel(saved.Width / 2, saved.Height / 2).ToArgb());
                    }
                    finally
                    {
                        if (savedPath is not null) File.Delete(savedPath);
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
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL INPUT setup: " + ex.Message); }
            finally { input.ReleaseInjectedInput(); CloseSmokeOverlays(); host.Close(); }
        };
        Application.Run(host);
        Console.WriteLine($"SELECTION INPUT RESULT: {successes} passed, {failures} failed");
        return failures == 0 && successes == 13 ? 0 : 1;
    }

    private static SessionController OverlaySession(CaptureOverlay overlay) => (SessionController)typeof(CaptureOverlay)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(overlay)!;
}
