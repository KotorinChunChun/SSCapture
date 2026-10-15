using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunWindowHitSmoke()
    {
        try
        {
            Rectangle monitor = CaptureNative.MonitorBounds()[0];
            using var target = new HitFixture(false) { Bounds = new(monitor.Left + 100, monitor.Top + 100, 360, 220), BackColor = Color.DarkGreen };
            using var cover = new HitFixture(true) { Bounds = new(monitor.Left + 80, monitor.Top + 80, 400, 260), BackColor = Color.Magenta, TransparencyKey = Color.Magenta };
            target.Show(); cover.Show(); Application.DoEvents();
            Point point = new(target.Left + 100, target.Top + 80);
            Rectangle actual = CaptureNative.WindowBounds(monitor).FirstOrDefault(r => r.Contains(point));
            Equal(target.Bounds, actual);
            Console.WriteLine("PASS NATIVE 単押し候補は透過補助ウィンドウを除外して対象フォームを選択");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL NATIVE " + ex.Message); return 1; }
    }

    private sealed class HitFixture : Form
    {
        private readonly bool transparent;
        internal HitFixture(bool transparent)
        {
            this.transparent = transparent;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= CaptureNative.WsExToolWindow | (transparent ? 0x20 : 0);
                return p;
            }
        }
        protected override bool ShowWithoutActivation => true;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CaptureNative.SetWindowPos(Handle, new(-1), Left, Top, Width, Height, 0x10);
        }
    }
}
