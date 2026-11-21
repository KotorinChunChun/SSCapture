using System.Runtime.InteropServices;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    // Explicit opt-in. Only synthetic forms; no screen pixels, clipboard, settings, or audio input are read.
    private static int RunFeedbackNativeSmoke(bool preview = false)
    {
        int successes = 0, failures = 0, clicks = 0;
        string stage = "テスト画面の前面化";
        using var host = new Form
        {
            Text = "SSCapture Feedback Fixture", ClientSize = new Size(640, 420),
            BackColor = Color.CornflowerBlue, AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false, MinimizeBox = false, TopMost = true
        };
        using var toast = new CaptureToastForm(location: () => (host.RectangleToScreen(host.ClientRectangle), 96));
        var opened = new List<string>();
        var edited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new ExternalApplication { Name = "検証用編集アプリ", Executable = Environment.ProcessPath! };
        string scratch = Path.Combine(Path.GetTempPath(), "sscapture-feedback-" + Guid.NewGuid().ToString("N"));
        using var feedback = new CaptureFeedback(createToast: () => toast, openSavedFile: opened.Add,
            launch: info => edited.TrySetResult(info.ArgumentList.Single()));
        using var refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        NativeSmokeInput? input = null;
        AppSettings settings = new() { PlayCaptureSound = false, HoldNotificationWhileApproaching = false };
        CaptureDelivery delivery = new(CaptureIntent.Screenshot, OutputDestination.Clipboard | OutputDestination.File, OutputDestination.None);
        host.MouseDown += (_, _) => clicks++;
        refresh.Tick += (_, _) => feedback.Notify(delivery, settings);
        host.Shown += async (_, _) =>
        {
            try
            {
                input = new NativeSmokeInput();
                await Task.Delay(150);
                NativeSmokeInput.FocusFixture(host.Handle);
                await Task.Delay(100);
                NativeSmokeInput.RequireForeground(host.Handle);
                stage = "通知表示後の非アクティブ確認";
                feedback.Notify(delivery, settings);
                await Task.Delay(150);
                Check(toast.Visible && FeedbackNative.IsWindowVisible(toast.Handle), feedback.LastWarning ?? "通知が表示されない");
                Check(FeedbackNative.GetWindowDisplayAffinity(toast.Handle, out uint affinity) && affinity == 0x11, "表示中の除外が0x11でない");
                Check(FeedbackNative.GetLayeredWindowAttributes(toast.Handle, out _, out byte alpha, out uint flags) && alpha == 255 && (flags & 2) != 0,
                    "表示中の通知が透明または不明");
                Check(host.RectangleToScreen(host.ClientRectangle).Contains(toast.Bounds), "合成ウィンドウの領域外");
                NativeSmokeInput.RequireForeground(host.Handle);
                Pass("表示中の実HWNDは除外0x11・不透明・非アクティブ");
                if (preview)
                {
                    refresh.Start();
                    Console.WriteLine($"PREVIEW READY host={host.Handle} toast={toast.Handle} affinity={affinity} alpha={alpha}");
                    return;
                }
                Point point = new(toast.Left + toast.Width / 2, toast.Top + toast.Height / 2);
                input.Move(point);
                await Task.Delay(80);
                RequireToastBodyTarget(point);
                input.LeftDown(); await Task.Delay(50); input.LeftUp(); await Task.Delay(100);
                Equal(1, clicks);
                NativeSmokeInput.RequireForeground(host.Handle);
                Pass("通知の上の実クリックが背後の合成フォームに届く");

                await Task.Delay(settings.NotificationDurationSeconds * 1000 + 150);
                Check(!toast.Visible && !FeedbackNative.IsWindowVisible(toast.Handle), "期限後も通知が表示中");
                Pass("実タイマーで指定の1秒後に非表示");

                stage = "非クリック通知の再表示と非表示後のフォーカス";
                settings = settings with { NotificationCorner = NotificationCorner.BottomLeft };
                feedback.Notify(delivery, settings);
                NativeSmokeInput.RequireForeground(host.Handle);
                Equal(host.PointToScreen(Point.Empty).X + 12, toast.Left);
                Check(toast.Visible, "左下への再表示に失敗");
                feedback.HideForCapture();
                NativeSmokeInput.RequireForeground(host.Handle);
                Check(!FeedbackNative.IsWindowVisible(toast.Handle), "撮影前に非表示にならない");
                Pass("左下への再表示と次回撮影前の非表示");

                feedback.Notify(delivery, settings with { NotificationDurationSeconds = 0 });
                Check(!FeedbackNative.IsWindowVisible(toast.Handle), "通知OFFなのに表示中");
                Pass("通知OFFの実ウィンドウ非表示");

                stage = "保存済み通知への切替時の非アクティブ確認";
                feedback.Notify(delivery with { SavedPath = "synthetic saved file.png" }, settings with { NotificationDurationSeconds = 5 });
                await Task.Delay(100);
                NativeSmokeInput.RequireForeground(host.Handle);
                Check(FeedbackNative.GetWindowDisplayAffinity(toast.Handle, out affinity) && affinity == 0x11, "クリック可能通知の除外がない");
                Point body = new(toast.Left + toast.Width / 2, toast.Top + 12);
                RequireToastBodyTarget(body);
                Point folder = PointOnAction("NotificationOpenFolder");
                input.Move(folder); await Task.Delay(60);
                stage = "保存済み通知の実クリック";
                input.LeftDown(); await Task.Delay(50); input.LeftUp(); await Task.Delay(100);
                Equal("synthetic saved file.png", opened.Single()); Equal(1, clicks);
                Check(!toast.Visible, "クリック後に消えない");
                NativeSmokeInput.RequireForeground(host.Handle);
                Pass("保存通知だけ実クリックを受け取り確定パスを一度渡す");

                stage = "保存通知の後のコピー通知が再びクリックを透過";
                feedback.Notify(delivery with { Completed = OutputDestination.Clipboard }, settings with { NotificationDurationSeconds = 5 });
                await Task.Delay(100);
                NativeSmokeInput.RequireForeground(host.Handle);
                input.Move(body); await Task.Delay(60);
                RequireToastBodyTarget(body);
                input.LeftDown(); await Task.Delay(50); input.LeftUp(); await Task.Delay(100);
                Equal(2, clicks); Equal(1, opened.Count);
                feedback.HideForCapture();
                Pass("保存通知の後のコピー通知は描画を保ちクリックを透過する");

                stage = "未保存通知のサムネイルをダブルクリック";
                using var sample = new Bitmap(64, 48);
                using (Graphics graphics = Graphics.FromImage(sample)) graphics.Clear(Color.DarkCyan);
                feedback.Notify(delivery with { Completed = OutputDestination.Clipboard, NotificationImage = new(sample, scratch) },
                    settings with { ImageEditors = [editor], NotificationApplicationId = editor.Id, NotificationDurationSeconds = 5 });
                Check(!Directory.Exists(scratch), "通知表示だけで未保存画像が書き込まれた");
                var previewPoint = PointOnAction("NotificationDragImage");
                input.Move(previewPoint); await Task.Delay(60);
                input.LeftDown(); input.LeftUp(); await Task.Delay(60);
                input.LeftDown(); input.LeftUp();
                string editPath = await edited.Task.WaitAsync(TimeSpan.FromSeconds(3));
                using (var image = new Bitmap(editPath))
                {
                    Equal(sample.Size, image.Size);
                    Equal(Color.DarkCyan.ToArgb(), image.GetPixel(32, 24).ToArgb());
                }
                File.Delete(editPath);
                Check(!toast.Visible, "編集開始後に通知が消えない");
                NativeSmokeInput.RequireForeground(host.Handle);
                Pass("未保存画像を実ダブルクリックし、編集コールバックのPNGと前面維持を確認");

                stage = "前回の撮影結果の再表示";
                feedback.ShowPreviousScreenshot(settings with { NotificationDurationSeconds = 5 });
                Check(toast.Visible && toast.Controls.Find("CaptureToastHeading", true).Single().Text == "前回の撮影結果",
                    "前回の撮影結果が表示されない");
                Check(!Application.OpenForms.OfType<ToastActionForm>().Any(form => form.Visible),
                    "前回の参考表示に操作ボタンが残る");
                NativeSmokeInput.RequireForeground(host.Handle);
                feedback.HideForCapture();
                Pass("前回の撮影結果を専用通知に表示し前面を維持");

                using var sound = new CompletionSound();
                sound.Request(); await sound.Pending;
                Check(sound.LastWarning is null, sound.LastWarning ?? "音声再生エラー");
                Pass("120msの効果音をWinMMで再生完了");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL FEEDBACK NATIVE {stage}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!preview || failures != 0) host.Close();
            }
        };
        try { Application.Run(host); }
        finally { refresh.Stop(); feedback.HideForCapture(); input?.Dispose(); }
        Console.WriteLine($"FEEDBACK NATIVE RESULT: {successes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;

        void Pass(string text) { successes++; Console.WriteLine("PASS FEEDBACK NATIVE " + text); }
        void RequireToastBodyTarget(Point point)
        {
            NativeSmokeInput.RequireForeground(host.Handle);
            nint actual = FeedbackNative.GetAncestor(FeedbackNative.WindowFromPoint(point), 2);
            Check(actual == host.Handle || actual == toast.Handle,
                $"クリック位置が検証画面の外です。host={host.Handle:X}, toast={toast.Handle:X}, actual={actual:X}");
        }
        static Point PointOnAction(string name)
        {
            var actions = Application.OpenForms.OfType<ToastActionForm>().Single(form => form.Visible);
            var control = actions.Controls.Find(name, true).Single();
            Check(control.Visible, "通知操作が表示されない: " + name);
            var point = control.PointToScreen(new Point(control.Width / 2, control.Height / 2));
            Check(FeedbackNative.GetAncestor(FeedbackNative.WindowFromPoint(point), 2) == actions.Handle,
                "クリック先が通知の操作ウィンドウではない: " + name);
            return point;
        }
    }

    private static class FeedbackNative
    {
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    }
}
