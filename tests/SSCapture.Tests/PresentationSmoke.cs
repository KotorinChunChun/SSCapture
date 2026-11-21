using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunPresentationSmoke()
    {
        using var host = new Form
        {
            Text = WindowTitle.Create("画面共有表示の検証"),
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(100, 100), new Size(420, 340)),
            TopMost = true, BackColor = Color.Blue
        };
        Exception? error = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(async () =>
        {
            try
            {
                NativeSmokeInput.FocusFixture(host.Handle);
                host.Update();
                Rectangle bounds = host.RectangleToScreen(host.ClientRectangle);
                Point Local(int x, int y) => bounds.Location + new Size(x, y);
                async Task<int> ReadBlue(Point point)
                {
                    await Task.Delay(90);
                    CaptureNative.SynchronizeDesktop();
                    using Bitmap frame = InstantCapture.Take(new Rectangle(point, new Size(1, 1)));
                    return frame.GetPixel(0, 0).B;
                }
                Point outside = Local(32, 32), inside = Local(100, 100);
                int clearBlue = await ReadBlue(outside);
                Check(clearBlue >= 220, $"検証用の青い画面が見えません（B={clearBlue}）");
                int clearInside = await ReadBlue(inside);
                var screenshot = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics draw = Graphics.FromImage(screenshot)) draw.Clear(Color.Red);
                var snapshot = new DesktopSnapshot(screenshot, bounds, [bounds]);
                var settings = new AppSettings { DimCaptureBackground = true, ShowPointerGuide = false, ShowSelectionGuide = false };
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File,
                    ShowSelectionGuide: false, OutputSettings: settings), snapshot, [], initialPointer: Local(60, 60),
                    confineCursor: _ => new System.IO.MemoryStream());
                try
                {
                    session.Start();
                    Equal(clearBlue, await ReadBlue(outside));
                    session.ChangeSharePresentation(true);
                    int dimmed = await ReadBlue(outside);
                    Check(dimmed > clearBlue * 0.45 && dimmed < clearBlue * 0.68, $"共有ONの暗転を画面取得で確認できません（B={dimmed}）");
                    using var native = new NativeSmokeInput();
                    native.Move(Local(60, 60));
                    await Task.Delay(50);
                    var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single();
                    NativeSmokeInput.RequireTarget(overlay.Handle, Local(60, 60));
                    native.LeftDown();
                    await Task.Delay(50);
                    native.Move(Local(210, 180));
                    Equal(clearInside, await ReadBlue(inside));
                    Equal(dimmed, await ReadBlue(outside));
                    native.LeftUp();
                    using CaptureOutcome outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                    Equal(new Size(150, 120), outcome.Image!.Size);
                    Equal(Color.Red.ToArgb(), outcome.Image.GetPixel(30, 30).ToArgb());
                    Equal(clearBlue, await ReadBlue(outside));
                }
                finally { session.Cancel(); }
                Console.WriteLine("PRESENTATION PASS 共有ON/OFF、暗転の内外、クリック透過、撮影元画素、終了後の消去");
            }
            catch (Exception ex) { error = ex; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }
}
