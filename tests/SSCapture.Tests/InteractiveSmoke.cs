using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    // Explicit opt-in only. This moves the real cursor and injects real key events.
    // It never reads desktop pixels, changes the clipboard, or writes an image.
    internal static int RunInteractiveSmoke()
    {
        int successes = 0;
        int failures = 0;
        bool completed = false;
        using var lifetime = new CancellationTokenSource();
        using var host = new Form
        {
            Text = "SSCapture Test Fixture",
            ClientSize = new Size(640, 420),
            BackColor = Color.White,
            AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true
        };
        NativeSmokeInput? input = null;
        host.FormClosing += (_, e) =>
        {
            if (completed) return;
            e.Cancel = true;
            lifetime.Cancel();
        };
        host.Shown += async (_, _) =>
        {
            try
            {
                input = new NativeSmokeInput();
                await Task.Delay(200, lifetime.Token);
                host.Activate();
                NativeSmokeInput.FocusFixture(host.Handle);
                await Task.Delay(100, lifetime.Token);
                NativeSmokeInput.RequireForeground(host.Handle);
                Rectangle bounds = host.RectangleToScreen(host.ClientRectangle);

                async Task<bool> Test(string name, Func<CancellationToken, Task> action)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await action(timeout.Token);
                        AssertNoCaptureOverlays();
                        successes++;
                        Console.WriteLine($"PASS INTERACTIVE {name}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Console.WriteLine($"FAIL INTERACTIVE {name}: {ex.GetType().Name}: {ex.Message}");
                        return false;
                    }
                    finally
                    {
                        input.ReleaseInjectedInput();
                        CloseSmokeOverlays();
                    }
                }

                async Task RectangleTest(bool reverse, CancellationToken token, bool splitMonitors = false)
                {
                    Point first = new(splitMonitors ? 220 : 10, 20);
                    Point last = new(splitMonitors ? 420 : 210, 140);
                    using Bitmap? result = await CaptureSynthetic(CaptureMode.Rectangle, bounds, input,
                        (overlay, ct) => Drag(overlay, bounds, input, reverse ? [last, first] : [first, last], ct), token, splitMonitors);
                    Check(result is not null, "矩形選択がキャンセルされた");
                    Equal(new Size(200, 120), result!.Size);
                    Equal(Color.Red.ToArgb(), result.GetPixel(20, 20).ToArgb());
                    Equal(Color.Blue.ToArgb(), result.GetPixel(180, 20).ToArgb());
                }

                if (!await Test("矩形 実マウスドラッグ 200x120 + 色", ct => RectangleTest(false, ct))) return;
                if (!await Test("矩形 逆方向ドラッグ 200x120 + 色", ct => RectangleTest(true, ct))) return;
                if (!await Test("矩形 2つのoverlay HWND間ドラッグ + 寸法/色", ct => RectangleTest(false, ct, true))) return;
                if (!await Test("Alt保持の矩形ドラッグ始点と終点をウィンドウ辺へスナップ", async ct =>
                {
                    Rectangle window = new(bounds.Left + 100, bounds.Top + 60, 220, 160);
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input,
                        async (overlay, token) =>
                        {
                            NativeSmokeInput.RequireForeground(overlay.Handle);
                            input.KeyDown(0x12);
                            try { await Drag(overlay, bounds, input, [new Point(108, 68), new Point(312, 212)], token); }
                            finally { input.KeyUp(0x12); }
                        }, ct, windows: [window]);
                    Equal(window, result.Selection!.Bounds);
                })) return;
                if (!await Test("固定右下基点はAlt押下解除だけで20pxスナップを切替", async ct =>
                {
                    Rectangle window = new(bounds.Left + 100, bounds.Top + 80, 250, 180);
                    Point pointer = new(window.Right - 8, window.Bottom - 9);
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input,
                        async (overlay, token) =>
                        {
                            var controller = (SessionController)typeof(CaptureOverlay).GetField("session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(overlay)!;
                            input.Move(pointer); await Task.Delay(100, token);
                            Equal(new Rectangle(pointer.X - 200, pointer.Y - 120, 200, 120), controller.Selection);
                            NativeSmokeInput.RequireForeground(overlay.Handle);
                            input.KeyDown(0x12); await Task.Delay(120, token);
                            Equal(new Rectangle(window.Right - 200, window.Bottom - 120, 200, 120), controller.Selection);
                            input.KeyUp(0x12); await Task.Delay(120, token);
                            Equal(new Rectangle(pointer.X - 200, pointer.Y - 120, 200, 120), controller.Selection);
                            input.KeyDown(0x12); await Task.Delay(80, token);
                            NativeSmokeInput.RequireTarget(overlay.Handle, pointer);
                            input.LeftDown(); await Task.Delay(50, token); input.LeftUp(); input.KeyUp(0x12);
                        }, ct, windows: [window], fixedSize: new Size(200, 120), fixedAnchor: CaptureAnchor.BottomRight);
                    Equal(new Rectangle(window.Right - 200, window.Bottom - 120, 200, 120), result.Selection!.Bounds);
                })) return;
                if (!await Test("固定サイズ 実左クリックだけで200x120を確定", async ct =>
                {
                    Point point = new(bounds.Left + 50, bounds.Top + 60);
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input,
                        async (overlay, token) =>
                        {
                            input.Move(point);
                            await Task.Delay(80, token);
                            NativeSmokeInput.RequireTarget(overlay.Handle, point);
                            input.LeftDown();
                            await Task.Delay(60, token);
                            input.LeftUp();
                        }, ct, fixedSize: new Size(200, 120));
                    Equal(new Rectangle(point, new Size(200, 120)), result.Selection!.Bounds);
                    Equal(new Size(200, 120), result.Image!.Size);
                    Equal(Color.Red.ToArgb(), result.Image.GetPixel(20, 20).ToArgb());
                    Equal(Color.Blue.ToArgb(), result.Image.GetPixel(180, 20).ToArgb());
                })) return;
                if (!await Test("固定サイズ 実矢印1px・Shift矢印10px・Enterで確定", async ct =>
                {
                    Point point = new(bounds.Left + 50, bounds.Top + 60);
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input,
                        async (overlay, token) =>
                        {
                            input.Move(point);
                            await Task.Delay(80, token);
                            NativeSmokeInput.RequireForeground(overlay.Handle);
                            input.KeyDown(0x27); input.KeyUp(0x27); // Right: 1 physical pixel.
                            await Task.Delay(60, token);
                            input.KeyDown(0xA0);
                            await Task.Delay(40, token);
                            input.KeyDown(0x28); input.KeyUp(0x28); // Shift+Down: 10 physical pixels.
                            input.KeyUp(0xA0);
                            await Task.Delay(80, token);
                            NativeSmokeInput.RequireForeground(overlay.Handle);
                            input.KeyDown(0x0D);
                            await Task.Delay(60, token);
                            input.KeyUp(0x0D);
                        }, ct, fixedSize: new Size(200, 120));
                    Equal(new Rectangle(point.X + 1, point.Y + 10, 200, 120), result.Selection!.Bounds);
                    Equal(new Size(200, 120), result.Image!.Size);
                })) return;
                if (!await Test("固定サイズ 右メニュー出力追加後も寸法維持", async ct =>
                {
                    Point point = new(bounds.Left + 50, bounds.Top + 60);
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.FixedRegion, bounds, input,
                        async (overlay, token) =>
                        {
                            input.Move(point);
                            await Task.Delay(80, token);
                            NativeSmokeInput.RequireTarget(overlay.Handle, point);
                            await OpenSelectionMenu(overlay, bounds, input, token);
                            CaptureContextMenu menu = overlay.SelectionMenu;
                            ToolStripMenuItem file = menu.Items.OfType<ToolStripMenuItem>()
                                .Single(item => Equals(item.Tag, OutputDestination.File));
                            Point itemPoint = menu.PointToScreen(new Point(file.Bounds.Left + file.Bounds.Width / 2,
                                file.Bounds.Top + file.Bounds.Height / 2));
                            input.Move(itemPoint);
                            await Task.Delay(60, token);
                            NativeSmokeInput.RequireMenuTarget(overlay.Handle, menu.Handle, itemPoint);
                            input.LeftDown();
                            await Task.Delay(60, token);
                            input.LeftUp();
                            await Task.Delay(160, token);
                            Check(!menu.Visible && !overlay.IsDisposed, "出力先変更で固定選択が終了した");
                            input.Move(point);
                            await Task.Delay(80, token);
                            NativeSmokeInput.RequireTarget(overlay.Handle, point);
                            input.LeftDown();
                            await Task.Delay(60, token);
                            input.LeftUp();
                        }, ct, fixedSize: new Size(200, 120));
                    Equal(new Rectangle(point, new Size(200, 120)), result.Selection!.Bounds);
                    Equal(new Size(200, 120), result.Image!.Size);
                    Equal(OutputDestination.Clipboard | OutputDestination.File, result.Selection.Destination);
                })) return;
                if (!await Test("自由形状 実マウス三角形 + 内外alpha", async ct =>
                {
                    using Bitmap? result = await CaptureSynthetic(CaptureMode.Freeform, bounds, input,
                        (overlay, token) => Drag(overlay, bounds, input,
                            [new(40, 40), new(240, 40), new(40, 240), new(40, 40)], token), ct);
                    Check(result is not null, "自由形状選択がキャンセルされた");
                    Equal(new Size(200, 200), result!.Size);
                    Equal(255, (int)result.GetPixel(20, 20).A);
                    Equal(Color.Red.ToArgb(), result.GetPixel(20, 20).ToArgb());
                    Equal(0, (int)result.GetPixel(170, 170).A);
                })) return;
                if (!await Test("左クリックで注入Z順のウィンドウ矩形を選択", async ct =>
                {
                    Rectangle front = new(bounds.Left + 30, bounds.Top + 40, 200, 140);
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input,
                        async (overlay, token) =>
                        {
                            Point point = new(bounds.Left + 80, bounds.Top + 80);
                            input.Move(point);
                            await Task.Delay(60, token);
                            NativeSmokeInput.RequireTarget(overlay.Handle, point);
                            input.LeftDown();
                            await Task.Delay(60, token);
                            input.LeftUp();
                        }, ct, windows: [front, bounds]);
                    Equal(front, result.Selection!.Bounds);
                    Equal(front.Size, result.Image!.Size);
                    Equal(Color.Red.ToArgb(), result.Image.GetPixel(20, 20).ToArgb());
                    Equal(Color.Blue.ToArgb(), result.Image.GetPixel(180, 20).ToArgb());
                })) return;
                if (!await Test("操作ガイドをタイトルで移動し選択を継続", async ct =>
                {
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input,
                        async (overlay, token) =>
                        {
                            var guide = Application.OpenForms.OfType<SelectionGuideForm>().Single();
                            guide.Activate();
                            await Task.Delay(80, token);
                            Point before = guide.Location;
                            Point title = new(guide.Left + 90, guide.Top + 12);
                            await Task.Run(async () =>
                            {
                                input.Move(title);
                                await Task.Delay(60, token);
                                NativeSmokeInput.RequireTarget(guide.Handle, title);
                                input.LeftDown();
                                await Task.Delay(80, token);
                                input.Move(new Point(title.X + 35, title.Y - 25));
                                await Task.Delay(100, token);
                                input.LeftUp();
                            }, token);
                            await Task.Delay(120, token);
                            Check(guide.Location != before, "ガイドが移動していない");
                            Check(!overlay.IsDisposed, "ガイド移動で撮影が終了した");
                            overlay.Activate();
                            await Task.Delay(80, token);
                            input.KeyDown(0x1B); input.KeyUp(0x1B);
                        }, ct);
                    Check(result.Selection is null, "最後のEscでキャンセルする");
                })) return;
                if (!await Test("録画操作窓を非表示から復元し開始・停止", async ct =>
                {
                    using var controls = new RecordingControlForm(bounds, new AppSettings(), false);
                    controls.StartRequested += controls.SetRecording;
                    controls.StopRequested += controls.Finish;
                    controls.Show(); controls.Hide();
                    WindowVisibility.Show(controls);
                    await Task.Delay(100, ct);
                    Check(controls.Visible, "録画操作窓が表示されていない");
                    var start = controls.Controls.Find("StartRecording", true).Single();
                    Point point = start.PointToScreen(new Point(start.Width / 2, start.Height / 2));
                    input.Move(point); input.LeftDown(); input.LeftUp();
                    await Task.Delay(100, ct);
                    var stop = controls.Controls.Find("StopRecording", true).Single();
                    Check(stop.Visible && stop.Enabled, "開始後に停止ボタンが表示されない");
                    point = stop.PointToScreen(new Point(stop.Width / 2, stop.Height / 2));
                    input.Move(point); input.LeftDown(); input.LeftUp();
                    await Task.Delay(100, ct);
                    Check(controls.IsDisposed, "停止で操作窓が終了しない");
                    host.Activate();
                })) return;
                if (!await Test("範囲確認は別窓にフォーカスが移ってもEscapeで登録画面へ戻る", async ct =>
                {
                    DisplayLayout[] displays = [new("synthetic", "fixture", bounds, 96, 96)];
                    var region = RegionCoordinates.FromBounds("検証用", new Rectangle(bounds.Left + 50, bounds.Top + 60, 240, 150), displays);
                    using var manager = new RegionManagerForm([region], region, _ => Task.FromResult<Rectangle?>(null), _ => null, () => displays);
                    manager.Show(); NativeSmokeInput.FocusFixture(manager.Handle); await Task.Delay(100, ct);
                    typeof(RegionManagerForm).GetMethod("PreviewRegion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(manager, null);
                    await Task.Delay(100, ct);
                    using var preview = Application.OpenForms.OfType<RegionPreviewForm>().Single();
                    Check(preview.Visible && !manager.Visible, "範囲確認へ遷移しない");
                    NativeSmokeInput.FocusFixture(host.Handle); await Task.Delay(100, ct);
                    NativeSmokeInput.RequireForeground(host.Handle);
                    input.KeyDown(0x1B); await Task.Delay(60, ct); input.KeyUp(0x1B); await Task.Delay(150, ct);
                    Check(preview.IsDisposed && manager.Visible, "Escapeで登録画面へ戻らない");
                    manager.Close(); host.Activate();
                })) return;
                if (!await Test("Escapeでキャンセル + overlay解放", async ct =>
                {
                    using Bitmap? result = await CaptureSynthetic(CaptureMode.Rectangle, bounds, input,
                        async (overlay, token) =>
                        {
                            NativeSmokeInput.RequireForeground(overlay.Handle);
                            input.KeyDown(0x1B);
                            await Task.Delay(60, token);
                            input.KeyUp(0x1B);
                        }, ct);
                    Check(result is null, "Escapeはnullを返すこと");
                })) return;
                if (!await Test("右メニューで保存を追加 → 中央クリックでモニタ全体", async ct =>
                {
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input,
                        async (overlay, token) =>
                        {
                            await OpenSelectionMenu(overlay, bounds, input, token);
                            CaptureContextMenu selectionMenu = overlay.SelectionMenu;
                            ToolStripMenuItem file = selectionMenu.Items.OfType<ToolStripMenuItem>()
                                .Single(item => Equals(item.Tag, OutputDestination.File));
                            Point itemPoint = selectionMenu.PointToScreen(new Point(file.Bounds.Left + file.Bounds.Width / 2,
                                file.Bounds.Top + file.Bounds.Height / 2));
                            input.Move(itemPoint);
                            await Task.Delay(60, token);
                            NativeSmokeInput.RequireMenuTarget(overlay.Handle, selectionMenu.Handle, itemPoint);
                            input.LeftDown();
                            await Task.Delay(60, token);
                            input.LeftUp();
                            await Task.Delay(160, token);
                            Check(!selectionMenu.Visible && !overlay.IsDisposed, "送信先変更は選択を継続すること");
                            Point capturePoint = new(bounds.Left + 70, bounds.Top + 70);
                            input.Move(capturePoint);
                            await Task.Delay(60, token);
                            NativeSmokeInput.RequireTarget(overlay.Handle, capturePoint);
                            input.MiddleDown();
                            await Task.Delay(60, token);
                            input.MiddleUp();
                        }, ct);
                    Equal(OutputDestination.Clipboard | OutputDestination.File, result.Selection!.Destination);
                    Equal(bounds, result.Selection.Bounds);
                    Equal(bounds.Size, result.Image!.Size);
                    Equal(Color.Red.ToArgb(), result.Image.GetPixel(20, 20).ToArgb());
                    Equal(Color.Blue.ToArgb(), result.Image.GetPixel(200, 20).ToArgb());
                })) return;
                if (!await Test("右メニュー表示中のEscapeで全選択キャンセル", async ct =>
                {
                    using CaptureOutcome result = await CaptureSyntheticOutcome(CaptureMode.Rectangle, bounds, input,
                        async (overlay, token) =>
                        {
                            await OpenSelectionMenu(overlay, bounds, input, token);
                            NativeSmokeInput.RequireMenuForeground(overlay.Handle, overlay.SelectionMenu.Handle);
                            input.KeyDown(0x1B);
                            await Task.Delay(60, token);
                            input.KeyUp(0x1B);
                        }, ct);
                    Check(result.Image is null && result.Selection is null, "メニュー中Escapeも選択をキャンセルすること");
                })) return;
                if (!await Test("nativeキーフック 初回1・repeat1・次回2", async ct =>
                {
                    host.Activate();
                    await Task.Delay(120, ct);
                    NativeSmokeInput.RequireForeground(host.Handle);
                    NativeSmokeInput.RequireNoHeldInput();
                    int captures = 0;
                    using var safety = new SafetyKeySuppressor();
                    using var hook = new KeyboardHook(() => Interlocked.Increment(ref captures));
                    try
                    {
                        input.KeyDown(0x5B);
                        await Task.Delay(60, ct);
                        input.KeyDown(0xA0);
                        await Task.Delay(60, ct);
                        NativeSmokeInput.RequireForeground(host.Handle);
                        input.KeyDown(0x53);
                        await WaitForCaptureCount(() => Volatile.Read(ref captures), 1, ct);
                        input.KeyDown(0x53);
                        await Task.Delay(180, ct);
                        Equal(1, Volatile.Read(ref captures));
                        input.KeyUp(0x53);
                        await Task.Delay(60, ct);
                        NativeSmokeInput.RequireForeground(host.Handle);
                        input.KeyDown(0x53);
                        await WaitForCaptureCount(() => Volatile.Read(ref captures), 2, ct);
                    }
                    finally
                    {
                        // Release all injected keys while this hook is still installed.
                        // Never send the shortcut after hook.Dispose().
                        input.ReleaseInjectedInput();
                    }
                    await Task.Delay(250, ct);
                    Equal(2, Volatile.Read(ref captures));
                    Equal(0, safety.LeakedKeys);
                    NativeSmokeInput.RequireForeground(host.Handle);
                })) return;
                if (!await Test("4枠nativeフック WinShiftS・PrintScreen・WinShiftR・CtrlShiftF8を識別", async ct =>
                {
                    host.Activate();
                    await Task.Delay(120, ct);
                    NativeSmokeInput.RequireForeground(host.Handle);
                    NativeSmokeInput.RequireNoHeldInput();
                    ConcurrentQueue<CaptureIntent> intents = new();
                    using var safety = new SafetyKeySuppressor();
                    using var hook = new KeyboardHook(intent => intents.Enqueue(intent),
                        new AppSettings { RecordingShortcut2 = "Ctrl+Shift+F8" });
                    List<CaptureIntent> expected = [];
                    async Task Chord(byte[] modifiers, byte key, CaptureIntent intent)
                    {
                        try
                        {
                            foreach (byte modifier in modifiers)
                            {
                                input.KeyDown(modifier);
                                await Task.Delay(40, ct);
                            }
                            NativeSmokeInput.RequireForeground(host.Handle);
                            input.KeyDown(key);
                            expected.Add(intent);
                            await WaitForCaptureCount(() => intents.Count, expected.Count, ct);
                            input.KeyDown(key);
                            await Task.Delay(80, ct);
                            Check(intents.ToArray().SequenceEqual(expected), "intent識別とrepeat抑止が一致すること");
                        }
                        finally { input.ReleaseInjectedInput(); }
                        await Task.Delay(80, ct);
                        NativeSmokeInput.RequireForeground(host.Handle);
                    }
                    try
                    {
                        await Chord([0x5B, 0xA0], 0x53, CaptureIntent.Screenshot);
                        await Chord([], 0x2C, CaptureIntent.Screenshot);
                        await Chord([0x5B, 0xA0], 0x52, CaptureIntent.Recording);
                        await Chord([0xA2, 0xA0], 0x77, CaptureIntent.Recording);
                    }
                    finally { input.ReleaseInjectedInput(); }
                    await Task.Delay(100, ct);
                    Equal(0, safety.LeakedKeys);
                    Check(intents.ToArray().SequenceEqual(expected), "解放後も余分なintentが増えないこと");
                })) return;
                if (!await Test("即時nativeフック1・0は対象番号だけを送り画面を変えない", async ct =>
                {
                    host.Activate(); await Task.Delay(120, ct);
                    NativeSmokeInput.RequireForeground(host.Handle);
                    NativeSmokeInput.RequireNoHeldInput();
                    ConcurrentQueue<int> displays = new(); int normal = 0;
                    using var safety = new SafetyKeySuppressor();
                    using var hook = new KeyboardHook(_ => Interlocked.Increment(ref normal), new AppSettings(), true, number => displays.Enqueue(number));
                    foreach (byte key in new byte[] { 0x31, 0x30 })
                    {
                        try
                        {
                            input.KeyDown(0x5B); await Task.Delay(40, ct);
                            input.KeyDown(0xA0); await Task.Delay(40, ct);
                            input.KeyDown(key);
                            await WaitForCaptureCount(() => displays.Count, key == 0x31 ? 1 : 2, ct);
                            input.KeyDown(key); await Task.Delay(60, ct);
                        }
                        finally { input.ReleaseInjectedInput(); }
                    }
                    Check(displays.ToArray().SequenceEqual(new[] { 1, 10 }), "番号またはrepeat不正");
                    Equal(0, normal); Equal(0, safety.LeakedKeys);
                    NativeSmokeInput.RequireForeground(host.Handle);
                })) return;
                if (!await Test("OCR文字入力欄のCtrlEnterは不正入力を拒否し有効な名前を確定", async ct =>
                {
                    using var bitmap = new Bitmap(20, 20);
                    using var naming = new OcrNameForm(bitmap, new AppSettings { FileNamePattern = "{ocr}_{##}.png" },
                        CaptureIntent.Screenshot, new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)),
                        (_, _, _) => Task.FromResult(new OcrResult(["合成 入力"])));
                    Exception? failure = null;
                    var interaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    naming.Shown += async (_, _) =>
                    {
                        try
                        {
                            var edit = (TextBox)naming.Controls.Find("OcrText1", true).Single();
                            NativeSmokeInput.FocusFixture(naming.Handle);
                            edit.Focus();
                            await Task.Delay(120, ct);
                            NativeSmokeInput.RequireForeground(naming.Handle);
                            NativeSmokeInput.RequireNoHeldInput();
                            Check(edit.Focused, "テキスト入力欄にフォーカスがない");
                            var pattern = (TextBox)naming.Controls.Find("OcrFileNamePattern", true).Single();
                            pattern.Text = "{unknown}.png";
                            input.KeyDown(0xA2); await Task.Delay(40, ct);
                            input.KeyDown(0x0D); input.KeyUp(0x0D); input.KeyUp(0xA2);
                            await Task.Delay(100, ct);
                            Check(naming.Modal && naming.DialogResult == DialogResult.None, "不正パターンのCtrlEnterで閉じた");
                            pattern.Text = "{ocr}_{##}.png";
                            edit.Text = "合成 入力";
                            NativeSmokeInput.RequireForeground(naming.Handle);
                            Check(edit.Focused, "入力中のフォーカスを維持");
                            input.KeyDown(0x0D); input.KeyUp(0x0D);
                            await Task.Delay(80, ct);
                            Equal(DialogResult.None, naming.DialogResult);
                            NativeSmokeInput.RequireForeground(naming.Handle);
                            input.KeyDown(0xA2); await Task.Delay(40, ct);
                            input.KeyDown(0x0D); input.KeyUp(0x0D); input.KeyUp(0xA2);
                            await WaitForCaptureCount(() => naming.DialogResult == DialogResult.OK ? 1 : 0, 1, ct);
                        }
                        catch (Exception ex) { failure = ex; }
                        finally
                        {
                            input.ReleaseInjectedInput();
                            if (naming.DialogResult != DialogResult.OK) naming.Close();
                            interaction.TrySetResult();
                        }
                    };
                    host.TopMost = false;
                    try
                    {
                        DialogResult result = naming.ShowDialog(host);
                        await interaction.Task.WaitAsync(ct);
                        if (failure is not null) throw failure;
                        Equal(DialogResult.OK, result);
                        Equal("合成入力_01.png", naming.FileName);
                        Check(naming.NamePlan is not null, "ボタンと同じ保存計画を確定");
                    }
                    finally { host.TopMost = true; }
                })) return;
                await Test("OCRモーダルの背後化後WinShiftSで同じ窓を復帰", async ct =>
                {
                    using var bitmap = new Bitmap(20, 20);
                    using var naming = new OcrNameForm(bitmap, new AppSettings { FileNamePattern = "{ocr}_{##}.png" }, CaptureIntent.Screenshot, DateTimeOffset.Now,
                        (_, _, _) => Task.FromResult(new OcrResult(["合成テスト"])));
                    int restored = 0, captures = 0; Exception? failure = null;
                    using var safety = new SafetyKeySuppressor();
                    using var hook = new KeyboardHook(_ => host.BeginInvoke(() =>
                    {
                        if (WindowVisibility.RestorePending(naming)) restored++;
                        else captures++;
                    }), new AppSettings());
                    naming.Shown += async (_, _) =>
                    {
                        using var cover = new Form { Text = "SSCapture foreground fixture", ClientSize = new Size(400, 200), StartPosition = FormStartPosition.CenterScreen };
                        try
                        {
                            cover.Show(); NativeSmokeInput.FocusFixture(cover.Handle); await Task.Delay(120, ct);
                            NativeSmokeInput.RequireForeground(cover.Handle);
                            NativeSmokeInput.RequireNoHeldInput();
                            input.KeyDown(0x5B); await Task.Delay(40, ct);
                            input.KeyDown(0xA0); await Task.Delay(40, ct);
                            input.KeyDown(0x53);
                            await WaitForCaptureCount(() => restored, 1, ct);
                            NativeSmokeInput.RequireForeground(naming.Handle);
                            Equal(0, captures); Equal(0, safety.LeakedKeys);
                            Check(naming.Modal, "モーダル状態を維持");
                            Equal(1, Application.OpenForms.OfType<OcrNameForm>().Count());
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { input.ReleaseInjectedInput(); cover.Close(); naming.Close(); }
                    };
                    host.TopMost = false;
                    try { naming.ShowDialog(host); }
                    finally { host.TopMost = true; }
                    if (failure is not null) throw failure;
                    await Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL INTERACTIVE setup: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                input?.ReleaseInjectedInput();
                CloseSmokeOverlays();
                input?.Dispose();
                input = null;
                completed = true;
                host.Close();
            }
        };
        try { Application.Run(host); }
        finally { input?.Dispose(); }
        Console.WriteLine($"INTERACTIVE RESULT: {successes} passed, {failures} failed");
        return failures == 0 && successes == 21 ? 0 : 1;
    }

    private static async Task<Bitmap?> CaptureSynthetic(CaptureMode mode, Rectangle bounds, NativeSmokeInput input,
        Func<CaptureOverlay, CancellationToken, Task> action, CancellationToken token, bool splitMonitors = false)
    {
        using CaptureOutcome outcome = await CaptureSyntheticOutcome(mode, bounds, input, action, token, splitMonitors);
        return outcome.Image is null ? null : (Bitmap)outcome.Image.Clone();
    }

    private static async Task<CaptureOutcome> CaptureSyntheticOutcome(CaptureMode mode, Rectangle bounds, NativeSmokeInput input,
        Func<CaptureOverlay, CancellationToken, Task> action, CancellationToken token, bool splitMonitors = false,
        IReadOnlyList<Rectangle>? windows = null, Size? fixedSize = null, CaptureAnchor fixedAnchor = CaptureAnchor.TopLeft,
        bool freeze = true, Func<DesktopSnapshot>? currentFrameFactory = null)
    {
        var pixels = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics graphics = Graphics.FromImage(pixels))
        {
            graphics.Clear(Color.Blue);
            graphics.FillRectangle(Brushes.Red, 0, 0, splitMonitors ? bounds.Width / 2 : 128, bounds.Height);
        }
        Rectangle[] monitors = splitMonitors
            ? [new(bounds.Left, bounds.Top, bounds.Width / 2, bounds.Height),
               new(bounds.Left + bounds.Width / 2, bounds.Top, bounds.Width - bounds.Width / 2, bounds.Height)]
            : [bounds];
        var snapshot = new DesktopSnapshot(pixels, bounds, monitors);
        Task<CaptureOutcome> capture = CaptureSession.SelectAsync(
            new CaptureOptions(mode, CaptureIntent.Screenshot, OutputDestination.Clipboard, FreezeFrame: freeze, FixedSize: fixedSize, FixedAnchor: fixedAnchor,
                LimitToStartingMonitor: false), snapshot, windows ?? [bounds], currentFrameFactory);
        bool returned = false;
        try
        {
            await Task.Delay(150, token);
            CaptureOverlay[] overlays = Application.OpenForms.OfType<CaptureOverlay>().ToArray();
            Equal(monitors.Length, overlays.Length);
            CaptureOverlay overlay = overlays.Single(candidate => candidate.MonitorBounds == monitors[0]);
            Equal(monitors[0], overlay.Bounds);
            NativeSmokeInput.FocusFixture(overlay.Handle);
            await Task.Delay(60, token);
            NativeSmokeInput.RequireForeground(overlay.Handle);
            await action(overlay, token);
            CaptureOutcome result = await capture.WaitAsync(token);
            returned = true;
            return result;
        }
        finally
        {
            input.ReleaseInjectedInput();
            CloseSmokeOverlays();
            if (!returned && capture.IsCompletedSuccessfully) capture.Result.Dispose();
        }
    }

    private static async Task OpenSelectionMenu(CaptureOverlay overlay, Rectangle bounds,
        NativeSmokeInput input, CancellationToken token)
    {
        Point point = new(bounds.Left + 80, bounds.Top + 80);
        input.Move(point);
        await Task.Delay(60, token);
        NativeSmokeInput.RequireTarget(overlay.Handle, point);
        input.RightDown();
        await Task.Delay(60, token);
        input.RightUp();
        await Task.Delay(160, token);
        Check(overlay.SelectionMenu.Visible, "右クリックメニューが表示されること");
    }

    private static async Task Drag(CaptureOverlay overlay, Rectangle bounds, NativeSmokeInput input,
        Point[] relativePoints, CancellationToken token)
    {
        Point PointOnScreen(Point point) => new(bounds.Left + point.X, bounds.Top + point.Y);
        Point first = PointOnScreen(relativePoints[0]);
        input.Move(first);
        await Task.Delay(60, token);
        NativeSmokeInput.RequireTarget(overlay.Handle, first);
        input.LeftDown();
        try
        {
            await Task.Delay(80, token);
            foreach (Point relative in relativePoints.Skip(1))
            {
                NativeSmokeInput.RequireForeground(overlay.Handle);
                input.Move(PointOnScreen(relative));
                await Task.Delay(100, token);
            }
        }
        finally { input.LeftUp(); }
    }

    private static async Task WaitForCaptureCount(Func<int> current, int expected, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (current() < expected && timer.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(20, token);
        Equal(expected, current());
    }

    private static void AssertNoCaptureOverlays() => Check(!Application.OpenForms.Cast<Form>().Any(form => form is CaptureOverlay or SelectionGuideForm), "撮影overlay/ガイドが残っている");

    private static void CloseSmokeOverlays()
    {
        // Only this process's synthetic capture forms; never close external windows.
        foreach (CaptureOverlay overlay in Application.OpenForms.OfType<CaptureOverlay>().ToArray()) overlay.Close();
    }

    // Installed first, so the actual KeyboardHook gets each key before this guard.
    // A broken tested hook must not let PrintScreen/Snipping Tool capture the real desktop.
    private sealed class SafetyKeySuppressor : IDisposable
    {
        private readonly HookCallback callback;
        private nint hook;
        internal int LeakedKeys { get; private set; }
        internal SafetyKeySuppressor()
        {
            callback = Handle;
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private nint Handle(int code, nint message, nint data)
        {
            if (code >= 0 && ((int)message is 0x0100 or 0x0101 or 0x0104 or 0x0105))
            {
                int key = Marshal.ReadInt32(data);
                if (key is 0x53 or 0x52 or 0x2C or 0x77 or 0x31 or 0x30)
                {
                    LeakedKeys++;
                    return 1;
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }
        public void Dispose()
        {
            if (hook == 0) return;
            UnhookWindowsHookEx(hook);
            hook = 0;
            GC.KeepAlive(callback);
        }
        private delegate nint HookCallback(int code, nint message, nint data);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint threadId);
        [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    }

    private sealed class NativeSmokeInput : IDisposable
    {
        private readonly Point originalCursor;
        private readonly List<byte> injectedKeys = [];
        private bool leftDown;
        private bool rightDown;
        private bool middleDown;
        private bool disposed;

        internal NativeSmokeInput()
        {
            RequireNoHeldInput();
            if (!GetCursorPos(out originalCursor)) throw new InvalidOperationException("カーソル位置を取得できません。");
        }

        internal static void RequireNoHeldInput()
        {
            foreach (int key in new[] { 0x01, 0x02, 0x04, 0x5B, 0x5C, 0x10, 0xA0, 0xA1, 0x11, 0xA2, 0xA3, 0x12, 0xA4, 0xA5, 0x53, 0x52, 0x2C, 0x77, 0x1B, 0x31, 0x30, 0x0D, 0x25, 0x26, 0x27, 0x28 })
                if ((GetAsyncKeyState(key) & 0x8000) != 0)
                    throw new InvalidOperationException("マウスボタンまたはテスト対象キーが押されているため自動入力を中止します。");
        }

        internal static void RequireForeground(nint window)
        {
            nint actual = GetForegroundWindow();
            if (actual != window) throw new InvalidOperationException($"テスト対象が最前面ではないため入力を中止します。expected={window:X}, actual={actual:X}");
        }

        internal static void FocusFixture(nint window)
        {
            ShowWindow(window, 5);
            uint ownThread = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            bool attached = foregroundThread != 0 && ownThread != foregroundThread && AttachThreadInput(ownThread, foregroundThread, true);
            try { SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
        }

        internal static void RequireTarget(nint window, Point point)
        {
            RequireForeground(window);
            if (WindowFromPoint(point) != window) throw new InvalidOperationException("クリック位置がテスト対象ではないため入力を中止します。");
        }

        internal static void RequireMenuForeground(nint owner, nint menu)
        {
            nint foreground = GetForegroundWindow();
            if (foreground != owner && foreground != menu)
                throw new InvalidOperationException("テスト対象メニューが最前面ではないため入力を中止します。");
        }

        internal static void RequireMenuTarget(nint owner, nint menu, Point point)
        {
            RequireMenuForeground(owner, menu);
            if (WindowFromPoint(point) != menu)
                throw new InvalidOperationException("クリック位置がテスト対象メニューではないため入力を中止します。");
        }

        internal void Move(Point point)
        {
            if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("カーソルを移動できません。");
        }

        internal void KeyDown(byte key)
        {
            if (!injectedKeys.Contains(key)) injectedKeys.Add(key);
            keybd_event(key, 0, key is 0x5B or 0x2C or >= 0x25 and <= 0x28 ? 1u : 0u, 0);
        }

        internal void KeyUp(byte key)
        {
            if (!injectedKeys.Remove(key)) return;
            keybd_event(key, 0, 2u | (key is 0x5B or 0x2C or >= 0x25 and <= 0x28 ? 1u : 0u), 0);
        }

        internal void LeftDown() { leftDown = true; mouse_event(0x0002, 0, 0, 0, 0); }
        internal void LeftUp() { if (!leftDown) return; leftDown = false; mouse_event(0x0004, 0, 0, 0, 0); }
        internal void RightDown() { rightDown = true; mouse_event(0x0008, 0, 0, 0, 0); }
        internal void RightUp() { if (!rightDown) return; rightDown = false; mouse_event(0x0010, 0, 0, 0, 0); }
        internal void MiddleDown() { middleDown = true; mouse_event(0x0020, 0, 0, 0, 0); }
        internal void MiddleUp() { if (!middleDown) return; middleDown = false; mouse_event(0x0040, 0, 0, 0, 0); }

        internal void ReleaseInjectedInput()
        {
            LeftUp();
            RightUp();
            MiddleUp();
            foreach (byte key in injectedKeys.AsEnumerable().Reverse().ToArray()) KeyUp(key);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReleaseInjectedInput();
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint source, uint target, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    }
}
