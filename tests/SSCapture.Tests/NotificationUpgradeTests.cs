using SSCapture.Services;
using SSCapture.Capture;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void NotificationUpgradeTests()
    {
        Run("通知は期限と同時に近接しても保持し離脱後1秒待つ", () =>
        {
            var lifetime = new NotificationLifetime(0, 1000);
            var bounds = new Rectangle(-1920, -100, 400, 100);
            Check(!lifetime.Expired(1000, new(-1950, -50), bounds, 96) && lifetime.Held, "期限が近接より優先した");
            Check(!lifetime.Expired(50_000, new(-1960, -50), bounds, 96), "解除幅48DIPを使っていない");
            Check(!lifetime.Expired(50_000, new(-1970, -50), bounds, 96) && !lifetime.Held, "離脱時に消えた");
            Check(!lifetime.Expired(50_999, Point.Empty, bounds, 96), "離脱猶予が短い");
            Check(lifetime.Expired(51_000, Point.Empty, bounds, 96), "期限に消えない");
        });
        Run("通知の接近幅はDPIに追従し残時間を保持する", () =>
        {
            var life = new NotificationLifetime(0, 5000); var bounds = new Rectangle(100, 100, 400, 100);
            Check(!life.Expired(500, new(40, 120), bounds, 192) && life.Held, "64pxで保持しない");
            Check(!life.Expired(20_000, new(10, 120), bounds, 192) && life.Held, "96pxで保持しない");
            Check(!life.Expired(20_000, new(0, 120), bounds, 192), "離脱時に残時間を失う");
            Check(!life.Expired(24_499, Point.Empty, bounds, 192), "残時間が縮んだ");
            Check(life.Expired(24_500, Point.Empty, bounds, 192), "残時間が復帰しない");
        });
        Run("通知タイマーは近接中だけ延長し撮影前には必ず止まる", () =>
        {
            long now = 0; Point pointer = new(80, 100);
            var toast = new FakeCaptureToast { DisplayBounds = new(100, 100, 400, 100) };
            var timer = new FakeFeedbackTimer();
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: timer,
                now: () => now, pointer: () => pointer);
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None), new AppSettings());
            Equal(50, timer.Milliseconds); now = 1000; timer.Fire(); Check(toast.Visible, "近接中に消えた");
            now = 100_000; timer.Fire(); Check(toast.Visible, "保持時間に上限がある");
            feedback.HideForCapture(); Check(!toast.Visible && !timer.Running, "撮影前も保持した");
            timer.Fire(); Check(!toast.Visible && !timer.Running, "古い期限で再表示した");
        });
        Run("暗転は新規・旧設定でOFFとなりON/OFFを保存", () => WithServiceDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json"); File.WriteAllText(path, "{}");
            var store = new SettingsStore(path); Check(!store.Load(out _).DimCaptureBackground, "旧設定が暗転した");
            foreach (bool value in new[] { true, false })
            { store.Save(new AppSettings { DimCaptureBackground = value }); Equal(value, store.Load(out _).DimCaptureBackground); }
        }));
        Run("暗転の描画は背景だけを変え原画像を変えない", () =>
        {
            foreach (bool dim in new[] { false, true })
            {
                var bounds = new Rectangle(0, 0, 100, 100);
                using var source = new Bitmap(100, 100); using (var g = Graphics.FromImage(source)) g.Clear(Color.White);
                using var snapshot = new DesktopSnapshot(source, bounds, [bounds]);
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                    DimCaptureBackground: dim), snapshot, []);
                using var menu = session.SelectionMenu; using var overlay = new CaptureOverlay(session, bounds);
                using var result = new Bitmap(100, 100); overlay.DrawToBitmap(result, bounds);
                Check(dim ? result.GetPixel(5, 5).R < 200 : result.GetPixel(5, 5).R == 255, "暗転切替が描画されない");
                Equal(Color.White.ToArgb(), snapshot.Image.GetPixel(5, 5).ToArgb());
            }
        });
        Run("暗転UIは最小設定画面に収まり独立して保存", () =>
        {
            AppSettings? result = null;
            using var form = new SettingsForm(new AppSettings(), next => { result = next; return null; }, () => { }, () => []);
            var field = (CheckBox)form.Controls.Find("DimCaptureBackground", true).Single(); field.Checked = true;
            typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
            Check(result!.DimCaptureBackground && result.FreezeFrame, "画面固定と連動した");
            form.Size = form.MinimumSize; _ = form.Handle;
            foreach (var child in Descendants(form)) _ = child.Handle;
            LayoutTree(form); Check(field.Parent!.ClientRectangle.Contains(field.Bounds), $"暗転欄が見切れる: {field.Bounds} / {field.Parent.ClientRectangle}");
        });
    }
}
