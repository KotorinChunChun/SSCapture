using SSCapture.Services;
using SSCapture.Capture;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void NotificationUpgradeTests()
    {
        Run("再起動の整理は通知用の正規名だけ削除し保存画像や他の作業を保持", () => WithServiceDirectory(directory =>
        {
            string folder = Path.Combine(directory, "notification-images"); Directory.CreateDirectory(folder);
            string owned = Path.Combine(folder, "notify-" + Guid.NewGuid().ToString("N") + ".png");
            string unrelated = Path.Combine(folder, "saved.png"); string otherWork = Path.Combine(directory, "recording.mp4");
            File.WriteAllText(owned, "fixture"); File.WriteAllText(unrelated, "fixture"); File.WriteAllText(otherWork, "fixture");
            NotificationImage.CleanupPreviousSession(directory);
            Check(!File.Exists(owned) && File.Exists(unrelated) && File.Exists(otherWork), "整理対象が通知用ファイルに限定されない");
        }));
        Run("通知用画像は履歴OFF・Clipboardのみでも余白と減色済み", () => WithServiceDirectory(directory => RunOutputTask(async () =>
        {
            CaptureDelivery? delivery = null; int history = 0;
            using var image = new Bitmap(20, 10);
            using var router = new OutputRouter(directory, copyImage: _ => Task.CompletedTask,
                captured: (_, _, _) => history++, delivered: d => delivery = d);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, new AppSettings
            { KeepHistory = false, ScreenshotPaddingPixels = 2, ScreenshotColorDepth = ScreenshotColorDepth.Indexed8,
                NotificationClickAction = NotificationClickAction.ExternalApplication });
            Equal(0, history); Check(delivery?.NotificationImage is not null, "通知用のメモリー画像がない");
            Check(!Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Any(), "開く前に画像を生成した");
            using var pending = delivery!.NotificationImage!;
            string path = await pending.PrepareAsync();
            using var output = new Bitmap(path);
            Equal(new Size(24, 14), output.Size);
            byte[] png = File.ReadAllBytes(path); Equal((byte)8, png[24]); Equal((byte)3, png[25]);
        })));
        Run("通知クリックで生成完了してから1回起動し未使用画像を解放", () => WithServiceDirectory(directory =>
        {
            using var bitmap = new Bitmap(10, 10);
            var pending = new NotificationImage(bitmap, directory);
            var app = new ExternalApplication { Id = "a", Name = "検証", Executable = Environment.ProcessPath! };
            var settings = new AppSettings { ImageEditors = [app], NotificationClickAction = NotificationClickAction.ExternalApplication, NotificationApplicationId = app.Id };
            var toast = new FakeCaptureToast(); var launched = new TaskCompletionSource(); int launches = 0;
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: new FakeFeedbackTimer(),
                launch: info => { using var completed = new Bitmap(info.ArgumentList[0]); Equal(10, completed.Width); launches++; launched.SetResult(); });
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, NotificationImage: pending), settings);
            Check(!Directory.EnumerateFiles(directory).Any(), "クリック前にディスクへ書いた");
            var click = toast.Activate!; click(); click(); WaitHistory(launched.Task); Equal(1, launches);
            var unused = new NotificationImage(bitmap, directory);
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, NotificationImage: unused), settings);
            feedback.HideForCapture();
            bool disposed = false; try { _ = unused.PrepareAsync(); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "通知消去後もメモリー画像を保持した");
        }));
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
        Run("通知の接近幅はDPIに追従し満額を保持する", () =>
        {
            var life = new NotificationLifetime(0, 5000); var bounds = new Rectangle(100, 100, 400, 100);
            Check(!life.Expired(500, new(40, 120), bounds, 192) && life.Held, "64pxで保持しない");
            Check(!life.Expired(20_000, new(10, 120), bounds, 192) && life.Held, "96pxで保持しない");
            Check(!life.Expired(20_000, new(0, 120), bounds, 192), "離脱時に残時間を失う");
            Check(!life.Expired(24_999, Point.Empty, bounds, 192), "残時間が縮んだ");
            Check(life.Expired(25_000, Point.Empty, bounds, 192), "満額の期限に消えない");
        });
        Run("遠くからの接近も満額保持しOFFは延長しない", () =>
        {
            var bounds = new Rectangle(1000, 1000, 400, 100);
            var life = new NotificationLifetime(0, 5000);
            Check(!life.Expired(100, Point.Empty, bounds, 96), "初回で消えた");
            Check(!life.Expired(4900, new(100, 100), bounds, 96) && life.Held, "接近を検出しない");
            Check(!life.Expired(8000, new(100, 100), bounds, 96), "満額を維持しない");
            Check(life.Expired(13000, new(100, 100), bounds, 96), "静止後も延長する");
            var off = new NotificationLifetime(0, 1000, false);
            Check(off.Expired(1000, bounds.Location, bounds, 96), "OFFで延長した");
        });
        Run("通知クリックは出力用とは別の登録アプリを一度だけ開く", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "image.png"); File.WriteAllText(path, "fixture");
            var toast = new FakeCaptureToast(); var launched = new List<System.Diagnostics.ProcessStartInfo>();
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: new FakeCompletionSound(), timer: new FakeFeedbackTimer(), launch: launched.Add);
            var app = new ExternalApplication { Id = "notify", Name = "通知用", Executable = Environment.ProcessPath! };
            var settings = new AppSettings { ImageEditors = [app], NotificationApplicationId = app.Id,
                NotificationClickAction = NotificationClickAction.ExternalApplication };
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, NotificationPath: path), settings);
            Action click = toast.Activate!; click(); click();
            Equal(1, launched.Count); Check(launched[0].ArgumentList.Contains(path), "通知画像を渡さない");
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: path), settings with { NotificationClickAction = NotificationClickAction.None });
            Check(toast.Activate is null, "何もしないにクリック動作が残る");
        }));
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
        Run("暗転は新規ON・省略された旧設定OFFを保ちON/OFFを保存", () => WithServiceDirectory(directory =>
        {
            Check(new AppSettings().DimCaptureBackground, "新規の暗転がONではない");
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
