using SSCapture.Capture;
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
                    var menu = overlay.SelectionMenu;
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
                await Test("Shift/Ctrl途中固定・同時固定・解除連続性", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        var session = OverlaySession(overlay);
                        await Down(overlay); await Move(overlay, 180, 140);
                        await Key(overlay, 0xA0, true); await Move(overlay, 230, 200);
                        Equal(new Size(150, 60), session.Selection.Size);
                        await Key(overlay, 0xA2, true); await Move(overlay, 260, 220);
                        Equal(new Size(150, 60), session.Selection.Size);
                        await Key(overlay, 0xA0, false); await Key(overlay, 0xA2, false);
                        Equal(new Size(150, 60), session.Selection.Size);
                        await Move(overlay, 270, 230); Equal(new Size(160, 70), session.Selection.Size);
                        input.LeftUp();
                    }, token);
                    Equal(new Size(160, 70), result.Image!.Size);
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
                        }, token);
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
                        await MenuClick(overlay, (ToolStripMenuItem)overlay.SelectionMenu.Items["Grid20"]!);
                        Check(session.Grid20, "メニュー20pxが反映されない");
                        await Down(overlay); await Move(overlay, 153, 137); input.LeftUp();
                    }, token);
                    Equal(new Size(80, 60), result.Image!.Size);
                });
                await Test("一時モード選択はセッション内で変更し画像を維持", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input, async (overlay, ct) =>
                    {
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        var root = (ToolStripMenuItem)overlay.SelectionMenu.Items["CaptureModeMenu"]!;
                        Point rootPoint = overlay.SelectionMenu.PointToScreen(new(root.Bounds.Left + 30, root.Bounds.Top + root.Bounds.Height / 2));
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
                        await Down(overlay, 120, 80); input.LeftUp();
                    }, token);
                    Equal(bounds, result.Selection!.Bounds);
                });
                await Test("固定サイズ中央クリックでモニタ全体", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input, async (overlay, ct) =>
                    {
                        await Move(overlay, 120, 80); NativeSmokeInput.RequireTarget(overlay.Handle, P(120, 80));
                        input.MiddleDown(); await Task.Delay(60); input.MiddleUp();
                    }, token, fixedSize: new(200, 120));
                    Equal(bounds, result.Selection!.Bounds);
                });
                await Test("メニューのデスクトップ全体はメニュー開始位置を使用", async token =>
                {
                    using var result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input, async (overlay, ct) =>
                    {
                        await OpenSelectionMenu(overlay, bounds, input, ct);
                        await MenuClick(overlay, (ToolStripMenuItem)overlay.SelectionMenu.Items["CaptureDesktop"]!);
                    }, token, fixedSize: new(200, 120));
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
                    }, token, windows: windows);
                    Equal(bounds, result.Selection!.Bounds);
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
                        }, token, freeze: freeze, currentFrameFactory: Future);
                        Equal(freeze ? 0 : 1, reads);
                        Equal((freeze ? Color.Red : Color.Green).ToArgb(), result.Image!.GetPixel(10, 10).ToArgb());
                    });
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL INPUT setup: " + ex.Message); }
            finally { input.ReleaseInjectedInput(); CloseSmokeOverlays(); host.Close(); }
        };
        Application.Run(host);
        Console.WriteLine($"SELECTION INPUT RESULT: {successes} passed, {failures} failed");
        return failures == 0 && successes == 10 ? 0 : 1;
    }

    private static SessionController OverlaySession(CaptureOverlay overlay) => (SessionController)typeof(CaptureOverlay)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(overlay)!;
}
