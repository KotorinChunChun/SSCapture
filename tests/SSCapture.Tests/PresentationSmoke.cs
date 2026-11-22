using SSCapture.Capture;
using SSCapture.Services;
using System.Runtime.InteropServices;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunPresentationSmoke(bool useNativeInput)
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
                async Task<Color> ReadPixel(Point point)
                {
                    await Task.Delay(90);
                    CaptureNative.SynchronizeDesktop();
                    using Bitmap frame = new(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                    using (Graphics draw = Graphics.FromImage(frame))
                    {
                        nint screenDc = PresentationProbe.GetDC(0), targetDc = draw.GetHdc();
                        try
                        {
                            Check(PresentationProbe.BitBlt(targetDc, 0, 0, 1, 1, screenDc, point.X, point.Y, 0x40cc0020),
                                "画面共有用レイヤーを含む画面を取得できません。");
                        }
                        finally
                        {
                            draw.ReleaseHdc(targetDc);
                            PresentationProbe.ReleaseDC(0, screenDc);
                        }
                    }
                    return frame.GetPixel(0, 0);
                }
                async Task<int> ReadBlue(Point point) => (await ReadPixel(point)).B;
                Point outside = Local(32, 32), inside = Local(100, 100);
                Color backdrop = Color.Empty;
                for (int attempt = 0; attempt < 6 && backdrop.ToArgb() != Color.Blue.ToArgb(); attempt++)
                    backdrop = await ReadPixel(outside);
                Equal(Color.Blue.ToArgb(), backdrop.ToArgb());
                int clearBlue = backdrop.B;
                int clearInside = await ReadBlue(inside);
                using (var surface = new CapturePresentation(bounds, host.Handle))
                {
                    Check(surface.Present(new Rectangle(Point.Empty, bounds.Size),
                        (graphics, dirty, _) => graphics.FillRectangle(Brushes.Green, dirty)), "描画レイヤーをWindowsに登録できません。");
                    Equal(0, await ReadBlue(outside));
                }
                var screenshot = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics draw = Graphics.FromImage(screenshot)) draw.Clear(Color.Red);
                var snapshot = new DesktopSnapshot(screenshot, bounds, [bounds]);
                var settings = new AppSettings { DimCaptureBackground = true, ShowPointerGuide = false, ShowSelectionGuide = false };
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File,
                    ShowSelectionGuide: false, OutputSettings: settings, DimCaptureBackground: true), snapshot, [], initialPointer: Local(60, 60),
                    confineCursor: _ => new System.IO.MemoryStream());
                try
                {
                    session.Start();
                    var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single();
                    NativeSmokeInput.FocusFixture(overlay.Handle);
                    int unshared = await ReadBlue(outside);
                    Check(Math.Abs(clearBlue - unshared) <= 5, $"共有OFFの背景に描画が写り込みました（B={unshared}、背景B={clearBlue}）。");
                    if (useNativeInput) NativeSmokeInput.RequireForeground(overlay.Handle);
                    session.ChangeSharePresentation(true);
                    int dimmed = await ReadBlue(outside);
                    Check(dimmed > clearBlue * 0.45 && dimmed < clearBlue * 0.68, $"共有ONの暗転を画面取得で確認できません（B={dimmed}）");
                    // The input overlay is raised at capture start and when the menu returns focus.
                    CaptureNative.RaiseWithoutActivation(overlay.Handle);
                    Check(CaptureNative.SetWindowDisplayAffinity(overlay.Handle, 0), "ローカル表示の検証を開始できません。");
                    try
                    {
                        Color local = await ReadPixel(outside);
                        Check(local.R is > 115 and < 175 && local.B < 5, $"入力画面の前面化後に手元の暗転が消えました（{local}）");
                    }
                    finally
                    {
                        _ = CaptureNative.SetWindowDisplayAffinity(overlay.Handle, CaptureNative.ExcludeFromCapture);
                    }
                    using var native = useNativeInput ? new NativeSmokeInput() : null;
                    if (native is not null)
                    {
                        native.Move(Local(60, 60));
                        await Task.Delay(50);
                        NativeSmokeInput.RequireTarget(overlay.Handle, Local(60, 60));
                        native.LeftDown();
                        await Task.Delay(50);
                        native.Move(Local(210, 180));
                    }
                    else
                    {
                        NativeSmokeInput.RequirePointTarget(overlay.Handle, Local(60, 60));
                        session.MouseDown(overlay, MouseButtons.Left, Local(60, 60));
                        session.MoveTo(Local(210, 180));
                    }
                    Check(Math.Abs(clearInside - await ReadBlue(inside)) <= 5, "選択範囲の内側が暗いままです。");
                    Check(Math.Abs(dimmed - await ReadBlue(outside)) <= 5, "選択範囲の外側の暗転が消えました。");
                    if (native is not null) native.LeftUp();
                    else session.MouseUp(MouseButtons.Left, Local(210, 180));
                    using CaptureOutcome outcome = await session.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                    Equal(new Size(150, 120), outcome.Image!.Size);
                    Equal(Color.Red.ToArgb(), outcome.Image.GetPixel(30, 30).ToArgb());
                    Check(Math.Abs(clearBlue - await ReadBlue(outside)) <= 5, "確定後も共有用の描画が残っています。");
                }
                finally { session.Cancel(); }

                Console.WriteLine("PRESENTATION PASS 共有ON/OFF、前面化後の手元の暗転、暗転の内外、クリック透過、撮影元画素、終了後の消去");
            }
            catch (Exception ex) { error = ex; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }

    private static class PresentationProbe
    {
        [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    }
}
