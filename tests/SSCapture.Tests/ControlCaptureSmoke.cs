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
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(100, 100), new Size(500, 380)),
            TopMost = true
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
                Point hit = expected.Location + new Size(expected.Width / 2, expected.Height / 2);
                input.Move(hit);
                var frame = new Bitmap(area.Width, area.Height);
                using (Graphics draw = Graphics.FromImage(frame)) draw.Clear(Color.CornflowerBlue);
                var window = new CaptureWindow(host.Bounds, host.Text, host.Handle,
                    (uint)Environment.ProcessId, NativeControlSmoke.GetWindowThreadProcessId(host.Handle, nint.Zero),
                    Owner: nint.Zero, ZOrder: 0, ClassName: "");
                var settings = new AppSettings
                {
                    CaptureBeforeKeys = new(Ctrl: CaptureKeyAction.SelectControl),
                    ShowSelectionGuide = false
                };
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File,
                    ShowSelectionGuide: false, OutputSettings: settings), new(frame, area, [area]), [],
                    initialPointer: hit, confineCursor: _ => new System.IO.MemoryStream(), captureWindowsKey: true,
                    findWindow: point => area.Contains(point) ? window : null);
                try
                {
                    session.Start();
                    await Task.Delay(100);
                    var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single();
                    NativeSmokeInput.RequireForeground(overlay.Handle);
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
                    input.LeftDown();
                    await Task.Delay(75);
                    input.LeftUp();
                    using var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                    input.KeyUp(0xA2);
                    Equal(expected, result.Selection!.Bounds);
                    Equal(expected.Size, result.Image!.Size);
                    Check(result.Selection.ReplaySource is CaptureReplaySource.Control, "部品の再撮影対象が記録されません。");
                    Equal(0, buttonClicks);
                    Check(focus.Focused, "撮影後に元の入力欄がフォーカスを失いました。");
                }
                finally { session.Cancel(); }
                Console.WriteLine("CONTROL PASS UIAの枠、入力フォーカス、クリック抑止、キー解放、撮影画像");
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

    private static class NativeControlSmoke
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, nint process);
    }
}
