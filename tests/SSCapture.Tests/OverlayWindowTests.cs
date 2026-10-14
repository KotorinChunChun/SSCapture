using System.Reflection;
using System.Runtime.InteropServices;
using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void OverlayWindowTests()
    {
        Run("範囲選択の実HWNDは初回・再作成後ともツールウィンドウでキー入力を妨げない", () =>
        {
            Rectangle bounds = new(0, 0, 320, 240);
            using var snapshot = new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]);
            var session = new SessionController(new CaptureOptions(CaptureMode.Rectangle,
                CaptureIntent.Screenshot, OutputDestination.None), snapshot, []);
            using var menu = session.SelectionMenu;
            using var overlay = new CaptureOverlay(session, bounds);
            AssertOverlayStyle(overlay);
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, null);
            AssertOverlayStyle(overlay);
            Equal(FormBorderStyle.None, overlay.FormBorderStyle);
            Check(!overlay.ShowInTaskbar && overlay.KeyPreview, "タスクバー非表示とキー入力を維持する");
        });
    }

    private static void AssertOverlayStyle(CaptureOverlay overlay)
    {
        int style = OverlayProbeNative.GetWindowLong(overlay.Handle, -20);
        Check((style & 0x80 /* WS_EX_TOOLWINDOW */) != 0, "自動配置の対象外となるツールウィンドウ属性がない");
        Check((style & 0x08000000 /* WS_EX_NOACTIVATE */) == 0, "キー入力に必要なアクティブ化が禁止されている");
    }

    // 明示指定のみ。単色画像を表示し、実画面・音声・クリップボードは取得しない。
    private static int RunOverlayPlacementSmoke()
    {
        var overlays = new List<CaptureOverlay>();
        try
        {
            IReadOnlyList<Rectangle> monitors = CaptureNative.MonitorBounds();
            Rectangle desktop = monitors.Aggregate(Rectangle.Union);
            using var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, monitors);
            using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.DarkSlateBlue);
            var session = new SessionController(new CaptureOptions(CaptureMode.Rectangle,
                CaptureIntent.Screenshot, OutputDestination.None), snapshot, []);
            using var menu = session.SelectionMenu;
            foreach (Rectangle monitor in monitors) overlays.Add(new CaptureOverlay(session, monitor));
            try
            {
                foreach (CaptureOverlay overlay in overlays) overlay.Show();
                // FancyZonesの非同期配置が処理される時間を含め、位置を複数回確認する。
                for (int sample = 0; sample < 3; sample++)
                {
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 250) { Application.DoEvents(); Thread.Sleep(10); }
                    foreach (CaptureOverlay overlay in overlays)
                    {
                        Check(!overlay.IsDisposed && overlay.Visible, "検証画面が途中で閉じられた");
                        Check(OverlayProbeNative.GetWindowRect(overlay.Handle, out var actual), "実HWNDの位置を取得できない");
                        Equal(overlay.MonitorBounds, Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom));
                        Equal(overlay.MonitorBounds.Size, overlay.ClientSize);
                        AssertOverlayStyle(overlay);
                        Check(OverlayProbeNative.GetProp(overlay.Handle, "FancyZones_MovedOnOpening") == 0,
                            "FancyZonesによる自動配置の対象になっている");
                    }
                }
                foreach (CaptureOverlay overlay in overlays)
                    Console.WriteLine($"PASS 実モニタ配置 {overlay.MonitorBounds} DPI={overlay.DeviceDpi} 自動移動なし");
                return 0;
            }
            finally { foreach (CaptureOverlay overlay in overlays) overlay.Dispose(); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL 実モニタ配置: {ex.Message}"); return 1; }
    }

    private static class OverlayProbeNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        internal static extern int GetWindowLong(nint window, int index);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetProp(nint window, string name);
    }
}
