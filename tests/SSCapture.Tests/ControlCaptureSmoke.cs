using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunControlCaptureSmoke()
    {
        using var host = new Form
        {
            Text = WindowTitle.Create("部品撮影の検証"), StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(100, 100), new Size(500, 380))
        };
        var focus = new TextBox { Name = "FixtureFocus", Bounds = new(40, 40, 300, 26) };
        var button = new Button { Name = "FixtureButton", Text = "保存", Bounds = new(60, 120, 230, 65) };
        int buttonClicks = 0;
        button.Click += (_, _) => buttonClicks++;
        host.Controls.AddRange([focus, button]);
        Exception? error = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(async () =>
        {
            try
            {
                using var input = new NativeSmokeInput();
                NativeSmokeInput.FocusFixture(host.Handle);
                focus.Focus();
                Rectangle area = host.RectangleToScreen(host.ClientRectangle);
                Rectangle expected = button.RectangleToScreen(button.ClientRectangle);
                Point hit = expected.Location + new Size(expected.Width / 4, expected.Height / 2);
                Point covered = expected.Location + new Size(expected.Width * 3 / 4, expected.Height / 2);
                using var cover = new ControlOccluder
                {
                    Text = "検証用の重なり", BackColor = Color.Magenta, TopMost = true, ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(covered - new Size(20, 20), new Size(40, 40))
                };
                cover.Show();
                NativeSmokeInput.FocusFixture(host.Handle);
                focus.Focus();
                CaptureNative.SynchronizeDesktop();
                using (var screen = InstantCapture.Take(new Rectangle(covered, new Size(2, 2))))
                    Equal(Color.Magenta.ToArgb(), screen.GetPixel(0, 0).ToArgb());
                input.Move(hit);
                var frame = new Bitmap(area.Width, area.Height);
                using (Graphics draw = Graphics.FromImage(frame))
                {
                    draw.Clear(Color.CornflowerBlue);
                    draw.FillRectangle(Brushes.Magenta, new Rectangle(cover.Location - new Size(area.Location), cover.Size));
                }
                var window = new CaptureWindow(host.Bounds, host.Text, host.Handle,
                    (uint)Environment.ProcessId, NativeControlSmoke.GetWindowThreadProcessId(host.Handle, nint.Zero),
                    Owner: nint.Zero, ZOrder: 0, ClassName: "");
                window = window with { Bounds = Rectangle.Intersect(CaptureNative.RequireCaptureWindow(window), area) };
                CaptureWindow occluder = CaptureNative.CaptureWindows(Screen.FromControl(host).Bounds).Single(target => target.Handle == cover.Handle);
                var settings = new AppSettings
                {
                    ShowSelectionGuide = false
                };
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File,
                    ShowSelectionGuide: false, OutputSettings: settings), new(frame, area, [area]), [occluder.Bounds, window.Bounds],
                    initialPointer: hit, confineCursor: _ => new System.IO.MemoryStream(), captureWindowsKey: true,
                    findWindow: point => cover.Bounds.Contains(point) ? occluder : area.Contains(point) ? window : null);
                try
                {
                    session.Start();
                    await Task.Delay(100);
                    var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single();
                    NativeSmokeInput.RequireForeground(overlay.Handle);
                    input.KeyDown(0x5B);
                    await WaitUntil(() => session.Mode == CaptureMode.Window, "Win押下中にウィンドウ撮影へ切り替わりません。");
                    input.KeyUp(0x5B);
                    await WaitUntil(() => session.Mode == CaptureMode.Rectangle, "Winを離しても元に戻りません。");
                    input.KeyDown(0xA2);
                    await WaitUntil(() => session.ControlBounds == expected, "部品の枠を取得できませんでした。");
                    Equal(host.Handle, CaptureNative.GetForegroundWindow());
                    Check(focus.Focused, "元の入力欄がフォーカスを失いました。");
                    NativeSmokeInput.RequirePointTarget(overlay.Handle, hit);
                    input.LeftDown();
                    await Task.Delay(75);
                    input.KeyUp(0xA2);
                    await WaitUntil(() => !session.SelectingControl, "キー解放後に部品の枠が消えません。");
                    input.LeftUp();
                    await Task.Delay(75);
                    Check(!session.Completion.IsCompleted && buttonClicks == 0, "途中のキー解放で撮影またはボタン操作が発生しました。");
                    input.KeyDown(0xA2);
                    await WaitUntil(() => session.ControlBounds == expected, "再度部品の枠を取得できませんでした。");
                    await WaitUntil(() => session.PreviewFrame?.Bounds.Contains(expected) == true, "コントロールのWGCプレビューがありません。");
                    input.Move(covered);
                    await WaitUntil(() => session.Pointer == covered, "重なり部分へポインタを移動できません。");
                    Equal(expected, session.ControlBounds);
                    using var rendered = new Bitmap(area.Width, area.Height);
                    overlay.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                    Point windowPoint = covered - new Size(session.PreviewFrame!.Bounds.Location);
                    int previewPixel = session.PreviewFrame.Image.GetPixel(windowPoint.X, windowPoint.Y).ToArgb();
                    Check(previewPixel != Color.Magenta.ToArgb(), "プレビューに手前のウィンドウが写りました。");
                    Point local = covered - new Size(area.Location);
                    Equal(previewPixel, rendered.GetPixel(local.X, local.Y).ToArgb());
                    input.LeftDown();
                    await Task.Delay(75);
                    input.LeftUp();
                    using var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                    input.KeyUp(0xA2);
                    Equal(expected, result.Selection!.Bounds);
                    Equal(expected.Size, result.Image!.Size);
                    Equal(previewPixel, result.Image.GetPixel(covered.X - expected.X, covered.Y - expected.Y).ToArgb());
                    Check(result.Selection.ReplaySource is CaptureReplaySource.Control { Isolated: true }, "コントロールのWGC再撮影対象が記録されません。");
                    Equal(0, buttonClicks);
                    Check(focus.Focused, "撮影後に元の入力欄がフォーカスを失いました。");
                }
                finally { session.Cancel(); }
                Console.WriteLine("CONTROL PASS Winの保持と解除、UIAの枠、重なり部分のWGCプレビューと画像、フォーカス維持");
            }
            catch (Exception ex) { error = ex; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }

    private static async Task WaitUntil(Func<bool> condition, string error)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException(error);
    }

    private static int RunControlDragSmoke()
    {
        using var host = new Form { Text = WindowTitle.Create("コントロール合成の検証"),
            StartPosition = FormStartPosition.Manual, Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(90, 90),
            ClientSize = new Size(420, 220) };
        var first = new Button { Name = "DragFirst", Text = "先頭", Bounds = new(30, 70, 80, 45) };
        var between = new Button { Name = "DragBetween", Text = "途中", Bounds = new(165, 70, 45, 45) };
        var last = new Button { Name = "DragLast", Text = "終点", Bounds = new(280, 70, 85, 45) };
        var outside = new Button { Name = "DragOutside", Text = "経路外", Bounds = new(165, 150, 45, 40) };
        host.Controls.AddRange([first, between, last, outside]);
        Exception? error = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(async () =>
        {
            try
            {
                Rectangle area = host.RectangleToScreen(host.ClientRectangle);
                var window = CaptureNative.CaptureWindows(area).Single(item => item.Handle == host.Handle);
                Rectangle a = first.RectangleToScreen(first.ClientRectangle);
                Rectangle b = between.RectangleToScreen(between.ClientRectangle);
                Rectangle c = last.RectangleToScreen(last.ClientRectangle);
                Point start = a.Location + new Size(10, 10), end = c.Location + new Size(10, 10);
                int clicked = 0;
                foreach (Control button in host.Controls) button.Click += (_, _) => clicked++;
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    ShowSelectionGuide: false), new DesktopSnapshot(new Bitmap(area.Width, area.Height), area, [area]),
                    [window.Bounds], initialPointer: start, findWindow: point => area.Contains(point) ? window : null);
                using var overlay = new CaptureOverlay(session, area);
                try
                {
                    _ = overlay.Handle;
                    session.MouseDown(overlay, MouseButtons.Left, start, Keys.Control);
                    session.Move(true, end, Keys.Control);
                    await WaitUntil(() => session.SelectedControlCount == 3, "実UIAでドラッグが通過した3件を取得できません。");
                    Check(session.TargetDragPath.SequenceEqual([start, end]), "ポインタの軌跡が一致しません。");
                    session.AbortSelection();
                    Check(!session.HasControlDrag && session.SelectedControlCount == 0 && !session.Completion.IsCompleted,
                        "取消後も軌跡または選択が残りました。");
                    session.MouseUp(MouseButtons.Left, end, Keys.None);
                    session.Move(true, start, Keys.None);
                    session.MouseDown(overlay, MouseButtons.Left, start, Keys.Control);
                    session.MouseUp(MouseButtons.Left, end, Keys.Control);
                    using var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(8));
                    Equal(Rectangle.Union(a, c), result.Selection!.Bounds);
                    Check(result.Selection.Composite!.Regions!.ToHashSet().SetEquals([a, b, c]), "撮影結果に経路外のコントロールが入りました。");
                    Equal(0, result.Image!.GetPixel(a.Width + 4, 10).A);
                    Check(result.Image.GetPixel(b.X - a.X + 4, 10).A > 0, "途中のコントロールが透過しています。");
                    Check(result.Selection.ReplaySource is CaptureReplaySource.ControlGroup { Isolated: true }, "WGC合成の対象が保持されていません。");
                    var group = (CaptureReplaySource.ControlGroup)result.Selection.ReplaySource!;
                    Check(group.Targets.All(target => target.Identity is not null), "再撮影で使うUIA識別子を取得できません。");
                    var settings = result.OutputSettings!;
                    var recipe = LastCaptureRecipe.Create(1, result.Selection.Bounds, null, settings, [], group);
                    host.Location += new Size(31, 24);
                    NativeSmokeInput.FocusFixture(host.Handle);
                    CaptureNative.SynchronizeDesktop();
                    var replay = await InstantCapture.TakeReplayWindowsAsync(recipe, settings);
                    using var replayImage = replay.Image;
                    var after = Rectangle.Union(first.RectangleToScreen(first.ClientRectangle),
                        last.RectangleToScreen(last.ClientRectangle));
                    Equal(after, replay.Bounds);
                    Equal(0, replayImage.GetPixel(a.Width + 4, 10).A);
                    Equal(0, clicked);
                }
                finally { session.Cancel(); }
                Console.WriteLine("CONTROL PASS ドラッグ軌跡・UIAの3対象・WGC合成・透過・取消・移動後の再撮影");
            }
            catch (Exception ex) { error = ex; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }

    private static int RunQuickControlOutputSmoke()
    {
        using var host = new Form
        {
            Text = WindowTitle.Create("コントロールの即時撮影を検証"), StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(100, 100), new Size(430, 300))
        };
        var button = new Button { Text = "保存", Bounds = new(50, 65, 178, 65) };
        host.Controls.Add(button);
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        Exception? error = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(async () =>
        {
            try
            {
                Rectangle area = host.RectangleToScreen(host.ClientRectangle);
                Rectangle expected = button.RectangleToScreen(button.ClientRectangle);
                Point hit = expected.Location + new Size(30, 25);
                CaptureWindow target = CaptureNative.CaptureWindows(area).Single(window => window.Handle == host.Handle);
                var settings = new AppSettings { DefaultDestination = OutputDestination.Clipboard, ShowSelectionGuide = false };
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    ShowSelectionGuide: false, OutputSettings: settings), new DesktopSnapshot(new Bitmap(area.Width, area.Height), area, [area]),
                    [target.Bounds], initialPointer: hit, findWindow: point => area.Contains(point) ? target : null);
                using var overlay = new CaptureOverlay(session, area);
                try
                {
                    Point local = hit - new Size(area.Location);
                    int coords = (local.Y << 16) | (local.X & 0xffff);
                    NativeControlSmoke.SendMessage(overlay.Handle, 0x0201, 0x0009, coords);
                    NativeControlSmoke.SendMessage(overlay.Handle, 0x0202, 0, coords);
                    Check(!session.SelectingControl && !session.Completion.IsCompleted, "左ボタンより先にCtrlを離しても撮影が取り消されません。");
                    // The HWND gets both mouse messages with MK_CONTROL. There is no physical Ctrl press.
                    NativeControlSmoke.SendMessage(overlay.Handle, 0x0201, 0x0009, coords);
                    Check(session.SelectingControl && session.HasPointerPress, "マウスメッセージのCtrlがコントロール選択に反映されません。");
                    NativeControlSmoke.SendMessage(overlay.Handle, 0x0202, 0x0008, coords);
                    Check(!session.Completion.IsCompleted && session.PointerHint?.Contains("コントロールを取得中", StringComparison.Ordinal) == true,
                        "すぐにクリックすると確定が取り消されます。");
                    session.ApplyPointerInput(hit, CaptureModifiers.None);
                    using var outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(6));
                    Equal(expected, outcome.Selection!.Bounds);
                    Equal(expected.Size, outcome.Image!.Size);
                    Equal(0, clicks);
                    int copies = 0, notifications = 0;
                    using var router = new OutputRouter(Path.GetTempPath(), copyImage: image =>
                    {
                        Equal(expected.Size, image.Size);
                        copies++;
                        return Task.CompletedTask;
                    }, delivered: delivery =>
                    {
                        using var thumbnail = delivery.NotificationImage;
                        Equal(OutputDestination.Clipboard, delivery.Completed);
                        Check(thumbnail is not null, "完了通知用のプレビューがありません。");
                        notifications++;
                    }, readDisplays: () => [new("検証用の画面", area, true)]);
                    await router.ScreenshotAsync(outcome.Image, outcome.Selection.Destination, outcome.OutputSettings!,
                        captureBounds: expected, replaySource: outcome.Selection.ReplaySource);
                    Equal(1, copies);
                    Equal(1, notifications);
                }
                finally { session.Cancel(); }
                Console.WriteLine("CONTROL QUICK PASS Ctrl付きの即時クリックを保持し、画像・コピー・完了通知へ渡した");
            }
            catch (Exception ex) { error = ex; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }

    private static class NativeControlSmoke
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, nint process);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static extern nint SendMessage(nint window, uint message, nint flags, nint position);
    }

    private sealed class ControlOccluder : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return parameters;
            }
        }
    }
}
