using System.Reflection;
using System.Runtime.InteropServices;
using SSCapture.Capture;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void OverlayWindowTests()
    {
        Run("配信用レイヤーは前面にした側も含めて各画面の入力ウィンドウより上に残る", () =>
        {
            Rectangle[] monitors = [new(0, 0, 160, 120), new(160, 0, 160, 120)];
            Rectangle bounds = Rectangle.Union(monitors[0], monitors[1]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                ShowSelectionGuide: false, DimCaptureBackground: true, LimitToStartingMonitor: false,
                OutputSettings: new() { ShareCapturePresentation = true }),
                new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, monitors), []);
            using var first = new CaptureOverlay(session, monitors[0]);
            using var second = new CaptureOverlay(session, monitors[1]);
            try
            {
                first.Show(); second.Show();
                first.Update(); second.Update();
                foreach (var active in new[] { first, second })
                {
                    CaptureNative.RaiseWithoutActivation(active.Handle);
                    Check(OwnsLayerAbove(first.Handle) && OwnsLayerAbove(second.Handle), "配信用レイヤーが入力ウィンドウに覆われた");
                }
            }
            finally { session.Cancel(); }

            static bool OwnsLayerAbove(nint owner)
            {
                for (nint window = OverlayProbeNative.GetWindow(owner, 3 /* GW_HWNDPREV */); window != 0;
                    window = OverlayProbeNative.GetWindow(window, 3))
                    if (OverlayProbeNative.GetWindow(window, 4 /* GW_OWNER */) == owner) return true;
                return false;
            }
        });
        Run("固定サイズの座標と寸法を一つにまとめて右クリックメニューと重ねない", () =>
        {
            Rectangle bounds = new(0, 0, 440, 320);
            Point pointer = new(100, 90);
            foreach (bool show in new[] { true, false })
            {
                var frame = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics graphics = Graphics.FromImage(frame)) graphics.Clear(Color.White);
                var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.None,
                    FixedSize: new(100, 80), ShowSelectionGuide: false, DimCaptureBackground: false,
                    LimitToStartingMonitor: false, OutputSettings: new() { ShowPointerGuide = show }),
                    new DesktopSnapshot(frame, bounds, [bounds]), [], initialPointer: pointer);
                try
                {
                    session.Start();
                    session.MoveTo(pointer);
                    var overlay = Application.OpenForms.OfType<CaptureOverlay>().Single(form => form.SelectionMenu == session.SelectionMenu);
                    using var rendered = new Bitmap(bounds.Width, bounds.Height);
                    void Draw() => overlay.DrawToBitmap(rendered, bounds);
                    Draw();
                    Equal(show ? "座標（100, 90　100 × 80）" : null, session.PointerHint);
                    Equal(Color.Black.ToArgb(), rendered.GetPixel(118, 114).ToArgb());
                    Equal(Color.White.ToArgb(), rendered.GetPixel(118, 142).ToArgb());
                    if (!show) continue;
                    session.MouseDown(overlay, MouseButtons.Right, pointer);
                    Check(session.SelectionMenu.Visible, "右クリックでメニューが出ない");
                    Draw();
                    Check(session.PointerHint is null, "メニューの表示中に座標案内が残った");
                    Equal(Color.White.ToArgb(), rendered.GetPixel(118, 114).ToArgb());
                    session.SelectionMenu.Close(ToolStripDropDownCloseReason.AppClicked);
                    session.MouseDown(overlay, MouseButtons.Left, pointer);
                    session.MoveTo(pointer);
                    Draw();
                    Equal("座標（100, 90　100 × 80）", session.PointerHint);
                    Equal(Color.Black.ToArgb(), rendered.GetPixel(118, 114).ToArgb());
                }
                finally { session.Cancel(); }
            }
        });
        Run("拡大鏡は負座標の画面端で境界両側の物理画素を表示する", () =>
        {
            Rectangle frameBounds = new(-400, -200, 320, 240);
            var viewport = new Rectangle(Point.Empty, frameBounds.Size);
            Point pointer = new(frameBounds.Right - 1, frameBounds.Top);
            Rectangle lens = CaptureMagnifier.Place(viewport, new(pointer.X - frameBounds.X, pointer.Y - frameBounds.Y));
            Check(viewport.Contains(lens), "拡大鏡が画面からはみ出した");
            Equal(new Size(80, 80), lens.Size);
            using var source = new Bitmap(frameBounds.Width, frameBounds.Height);
            using var destination = new Bitmap(viewport.Width, viewport.Height);
            source.SetPixel(319, 0, Color.Lime);
            source.SetPixel(318, 0, Color.Red);
            using (Graphics graphics = Graphics.FromImage(destination))
                CaptureMagnifier.Draw(graphics, lens, source, frameBounds, pointer);
            int x = lens.Left + lens.Width / 2, y = lens.Top + lens.Height / 2;
            int halfPixel = CaptureMagnifier.DefaultZoom / 2;
            Equal(Color.Lime.ToArgb(), destination.GetPixel(x + halfPixel, y + halfPixel).ToArgb());
            Equal(Color.Red.ToArgb(), destination.GetPixel(x - halfPixel, y + halfPixel).ToArgb());
            Equal(Color.DimGray.ToArgb(), destination.GetPixel(x + halfPixel, y - halfPixel).ToArgb());
        });
        Run("暗転OFFでは左押下中もドラッグ中も暗転しない", () =>
        {
            Rectangle bounds = new(0, 0, 320, 240);
            Rectangle first = new(35, 45, 96, 108), second = new(10, 20, 200, 180);
            using var snapshot = new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]);
            using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.White);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                DimCaptureBackground: false), snapshot, [first, second]);
            using var menu = session.SelectionMenu;
            using var overlay = new CaptureOverlay(session, bounds);
            using var rendered = new Bitmap(bounds.Width, bounds.Height);
            var gesture = (SelectionGesture)typeof(SessionController).GetField("gesture",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var start = new Point(first.Left + 1, 80);
            gesture.Begin(start, new(12, 12));
            gesture.Move(new(first.Left - 1, start.Y));
            Equal(first, session.HighlightedWindow);
            overlay.DrawToBitmap(rendered, bounds);
            Equal(Color.White.ToArgb(), rendered.GetPixel(45, 56).ToArgb());
            Equal(Color.White.ToArgb(), rendered.GetPixel(15, 10).ToArgb());

            gesture.Move(new(200, 180));
            Equal(Rectangle.Empty, session.HighlightedWindow);
            overlay.DrawToBitmap(rendered, bounds);
            Equal(Color.White.ToArgb(), rendered.GetPixel(15, 10).ToArgb());
        });
        Run("ポインタガイドは画面端のガイドと独立し、Win解放の操作を改行して案内する", () =>
        {
            Rectangle bounds = new(0, 0, 320, 240);
            foreach (bool showPointerGuide in new[] { false, true })
            {
                var snapshot = new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]);
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                    ShowSelectionGuide: false, OutputSettings: new() { ShowPointerGuide = showPointerGuide }), snapshot, []);
                try
                {
                    session.ApplyPointerInput(new(100, 80), CaptureModifiers.Win);
                    Equal(showPointerGuide ? "ウィンドウ撮影中\nキーを離すと元の撮影モードに戻る　Escで取り消し" : null, session.PointerHint);
                    Check(!session.ShowSelectionGuide, "ポインタガイドが画面端の設定に影響した");
                }
                finally { session.Cancel(); }
            }
        });
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
        Run("解像度と原点が異なるモニターの背景はそれぞれの画像を表示する", () =>
        {
            Rectangle[] monitors = [new(-400, -80, 400, 300), new(0, 0, 640, 480)];
            Rectangle desktop = monitors.Aggregate(Rectangle.Union);
            using var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, monitors);
            Color[] colors = [Color.Red, Color.Blue];
            using (Graphics graphics = Graphics.FromImage(snapshot.Image))
            {
                for (int index = 0; index < monitors.Length; index++)
                {
                    Rectangle area = monitors[index];
                    area.Offset(-desktop.X, -desktop.Y);
                    using var brush = new SolidBrush(colors[index]);
                    graphics.FillRectangle(brush, area);
                }
            }
            var session = new SessionController(new CaptureOptions(CaptureMode.Rectangle,
                CaptureIntent.Screenshot, OutputDestination.None), snapshot, []);
            using var menu = session.SelectionMenu;
            for (int index = 0; index < monitors.Length; index++)
            {
                using var overlay = new CaptureOverlay(session, monitors[index]);
                using var rendered = new Bitmap(monitors[index].Width, monitors[index].Height);
                overlay.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                foreach (Point point in new[] { new Point(5, 5), new Point(rendered.Width - 6, rendered.Height - 6) })
                {
                    Color pixel = rendered.GetPixel(point.X, point.Y);
                    Check(index == 0 ? pixel.R > 100 && pixel.B == 0 : pixel.B > 100 && pixel.R == 0,
                        $"モニター{index + 1}の{point}に別モニターの画像または空白が描かれた: {pixel}");
                }
            }
        });
    }

    private static void AssertOverlayStyle(CaptureOverlay overlay)
    {
        int style = OverlayProbeNative.GetWindowLong(overlay.Handle, OverlayProbeNative.ExtendedStyleIndex);
        Check((style & OverlayProbeNative.ToolWindowStyle) != 0, "自動配置の対象外となるツールウィンドウ属性がない");
        Check((style & OverlayProbeNative.NoActivateStyle) == 0, "キー入力に必要なアクティブ化が禁止されている");
    }

    // 明示指定のみ。単色画像を表示し、実画面・音声・クリップボードは取得しない。
    private static int RunOverlayPlacementSmoke()
    {
        try
        {
            IReadOnlyList<Rectangle> monitors = CaptureNative.MonitorBounds();
            Rectangle desktop = monitors.Aggregate(Rectangle.Union);
            using var snapshot = new DesktopSnapshot(new Bitmap(desktop.Width, desktop.Height), desktop, monitors);
            using (Graphics graphics = Graphics.FromImage(snapshot.Image)) graphics.Clear(Color.DarkSlateBlue);
            var session = new SessionController(new CaptureOptions(CaptureMode.Rectangle,
                CaptureIntent.Screenshot, OutputDestination.None), snapshot, []);
            using var menu = session.SelectionMenu;
            var overlays = new List<CaptureOverlay>();
            try
            {
                foreach (Rectangle monitor in monitors) overlays.Add(new CaptureOverlay(session, monitor));
                foreach (CaptureOverlay overlay in overlays) overlay.Show();
                // FancyZonesの非同期配置が処理される時間を含め、位置を複数回確認する。
                for (int sample = 0; sample < 3; sample++)
                {
                    PumpOverlayMessages(TimeSpan.FromMilliseconds(250));
                    foreach (CaptureOverlay overlay in overlays) AssertOverlayPlacement(overlay);
                }
                foreach (CaptureOverlay overlay in overlays)
                    Console.WriteLine($"PASS 実モニター配置 {overlay.MonitorBounds} DPI={overlay.DeviceDpi} 自動移動なし");
                return 0;
            }
            finally { foreach (CaptureOverlay overlay in overlays) overlay.Dispose(); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL 実モニター配置: {ex.Message}"); return 1; }
    }

    private static void PumpOverlayMessages(TimeSpan duration)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < duration) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void AssertOverlayPlacement(CaptureOverlay overlay)
    {
        Check(!overlay.IsDisposed && overlay.Visible, "検証画面が途中で閉じられた");
        Check(OverlayProbeNative.GetWindowRect(overlay.Handle, out var actual), "実HWNDの位置を取得できない");
        Equal(overlay.MonitorBounds, Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom));
        Equal(overlay.MonitorBounds.Size, overlay.ClientSize);
        AssertOverlayStyle(overlay);
        Check(OverlayProbeNative.GetProp(overlay.Handle, OverlayProbeNative.FancyZonesMovedProperty) == 0,
            "FancyZonesによる自動配置の対象になっている");
    }

    private static class OverlayProbeNative
    {
        internal const int ExtendedStyleIndex = -20;
        internal const int ToolWindowStyle = 0x00000080;
        internal const int NoActivateStyle = 0x08000000;
        internal const string FancyZonesMovedProperty = "FancyZones_MovedOnOpening";
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        internal static extern int GetWindowLong(nint window, int index);
        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetProp(nint window, string name);
    }
}
