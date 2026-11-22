using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunTimerNativeSmoke(string directory)
    {
        Directory.CreateDirectory(directory);
        Rectangle workArea = Screen.PrimaryScreen!.WorkingArea;
        using var target = new Form
        {
            Text = WindowTitle.Create("予約撮影の対象"), StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(workArea.Location + new Size(80, 100), new Size(380, 280)),
            BackColor = Color.Cyan, TopMost = true
        };
        using var obstruction = new Form
        {
            Text = WindowTitle.Create("予約撮影の前面ウィンドウ"), StartPosition = FormStartPosition.Manual,
            BackColor = Color.OrangeRed, TopMost = true
        };
        Exception? error = null;
        target.Shown += (_, _) => target.BeginInvoke((Action)(async () =>
        {
            try
            {
                await Task.Delay(120);
                Rectangle desktop = CaptureNative.MonitorBounds().Aggregate(Rectangle.Union);
                CaptureWindow before = CaptureNative.CaptureWindows(desktop).Single(window => window.Handle == target.Handle);
                var settings = new AppSettings
                {
                    SaveDirectory = directory, FileNamePattern = "timer_{##}.{ext}",
                    DefaultDestination = OutputDestination.File, WindowCrop = new(3, 4, 5, 6)
                };
                var selection = new CaptureSelection(before.Bounds, null, OutputDestination.File,
                    ReplaySource: new CaptureReplaySource.Windows([before], Isolated: true, IncludeOwned: false, IsSingleWindowSelection: true));
                var shot = CaptureTimerShot.FromSelection(selection, settings, "予約");

                target.Location += new Size(140, 88);
                target.Update();
                obstruction.Bounds = Rectangle.Inflate(target.Bounds, 15, 15);
                obstruction.Show();
                NativeSmokeInput.FocusFixture(obstruction.Handle);
                NativeSmokeInput.RequireForeground(obstruction.Handle);
                CaptureNative.SynchronizeDesktop();
                Rectangle moved = CaptureNative.RequireCaptureWindow(before);
                Check(moved.Location != before.Bounds.Location, "予約対象が移動しませんでした。");

                using var output = new OutputRouter(Path.Combine(directory, "work"));
                using var reservation = new CaptureTimerPump(new CaptureSchedule.Delay(1),
                    async capturedAt => _ = await shot.DeliverAsync(output, capturedAt));
                await reservation.RunAsync().WaitAsync(TimeSpan.FromSeconds(8));
                using var png = new Bitmap(Directory.GetFiles(directory, "timer_*.png").Single());
                Equal(new Size(moved.Width - 11, moved.Height - 7), png.Size);
                Equal(Color.Cyan.ToArgb(), png.GetPixel(png.Width / 2, png.Height / 2).ToArgb());
                Console.WriteLine("TIMER PASS 移動後のウィンドウを追跡し、前面の別ウィンドウを除いて四辺を切り抜き");
            }
            catch (Exception ex) { error = ex; }
            finally { obstruction.Close(); target.Close(); }
        }));
        Application.Run(target);
        if (error is null) return 0;
        Console.Error.WriteLine(error);
        return 1;
    }
}
