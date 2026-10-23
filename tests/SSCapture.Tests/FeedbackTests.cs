using System.Reflection;
using System.Text;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void FeedbackTests()
    {
        Run("通知先はメイン・撮影面積・指定DISPLAYで決まり切断時はメイン", () =>
        {
            NotificationMonitor[] screens =
            [
                new(@"\\.\DISPLAY3", new(-1920, -200, 1920, 1080), new(-1920, -200, 1920, 1040), false),
                new(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), true),
                new(@"\\.\DISPLAY10", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), false)
            ];
            Rectangle capture = new(-1000, 100, 300, 200);
            Equal(screens[1], NotificationPlacement.Resolve(NotificationDisplay.Main, capture, screens));
            Equal(screens[0], NotificationPlacement.Resolve(NotificationDisplay.Captured, capture, screens));
            Equal(screens[0], NotificationPlacement.Resolve(NotificationDisplay.Captured, new Rectangle(-300, 0, 500, 200), screens));
            Equal(screens[1], NotificationPlacement.Resolve(NotificationDisplay.Captured, new Rectangle(-100, 0, 200, 200), screens));
            Equal(screens[2], NotificationPlacement.Resolve(NotificationDisplay.Display10, capture, screens));
            Equal(screens[1], NotificationPlacement.Resolve(NotificationDisplay.Display2, capture, screens));
            Equal(screens[1], NotificationPlacement.Resolve(NotificationDisplay.Captured, null, screens));
            Equal(screens[1], NotificationPlacement.Resolve(NotificationDisplay.Captured, new Rectangle(9999, 9999, 10, 10), screens));
        });

        Run("保存通知だけにクリック先を付け最新パスを1回開く", () =>
        {
            var toast = new FakeCaptureToast(); var sound = new FakeCompletionSound(); var timer = new FakeFeedbackTimer();
            var opened = new List<string>();
            using var feedback = new CaptureFeedback(createToast: () => toast, sound: sound, timer: timer, openSavedFile: opened.Add);
            var settings = new AppSettings { OpenFolderAfterSave = false, NotificationDurationSeconds = 5, NotificationDisplay = NotificationDisplay.Display3 };
            var bounds = new Rectangle(-700, -200, 300, 100);
            var saved = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: "first.png", CaptureBounds: bounds);
            feedback.Notify(saved, settings);
            Equal(5000, timer.Milliseconds); Equal(NotificationDisplay.Display3, toast.Display); Equal<Rectangle?>(bounds, toast.CaptureBounds);
            Action first = toast.Actions!.OpenFolder!;
            feedback.Notify(saved with { SavedPath = "second.png" }, settings);
            first(); Equal(0, opened.Count);
            Action second = toast.Actions!.OpenFolder!;
            second(); second(); Equal("second.png", opened.Single());
            Check(!toast.Visible && !timer.Running, "クリック後も通知が残る");
            feedback.Notify(saved, settings); Action expired = toast.Actions!.OpenFolder!;
            timer.Fire(); expired(); Equal(1, opened.Count);
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None), settings);
            Check(toast.Actions!.OpenFolder is null, "コピーだけでファイルを開く");
            feedback.Notify(saved, settings); Action superseded = toast.Actions!.OpenFolder!;
            feedback.Notify(saved, settings with { NotificationDurationSeconds = 0 });
            superseded(); Equal(1, opened.Count); Check(!toast.Visible, "0秒でも表示が残る");
        });

        Run("通知クリックの表示失敗は成功結果を変更せず警告する", () =>
        {
            string? warning = null; var toast = new FakeCaptureToast();
            using var feedback = new CaptureFeedback(text => warning = text, () => toast, new FakeCompletionSound(), new FakeFeedbackTimer(),
                _ => throw new FileNotFoundException("保存ファイルが移動されています"));
            feedback.Notify(new(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: "removed.png"), new AppSettings());
            toast.Actions!.OpenFolder!();
            Check(warning?.Contains("Explorer") == true && !toast.Visible, "クリック失敗を処理できない");
        });

        Run("完了表示は負座標と作業領域を保持しDPI別の右下左下に収まる", () =>
        {
            Rectangle work = new(-2560, -200, 2560, 1360);
            foreach (int dpi in new[] { 96, 120, 144, 192 })
                foreach (NotificationCorner corner in new[] { NotificationCorner.BottomRight, NotificationCorner.BottomLeft })
                {
                    Rectangle bounds = CaptureToastForm.CalculateBounds(work, dpi, corner);
                    int margin = (int)Math.Round(12 * dpi / 96d);
                    Check(work.Contains(bounds), "通知が作業領域外");
                    Equal((int)Math.Round(392 * dpi / 96d), bounds.Width);
                    Equal(work.Bottom - margin, bounds.Bottom);
                    Equal(corner == NotificationCorner.BottomLeft ? work.Left + margin : work.Right - margin - bounds.Width, bounds.Left);
                }
            Equal(new Rectangle(-1, -1, 1, 1), CaptureToastForm.CalculateBounds(new Rectangle(-1, -1, 1, 1), 192, NotificationCorner.BottomLeft));
            bool rejected = false;
            try { CaptureToastForm.CalculateBounds(new Rectangle(int.MaxValue - 10, 0, 100, 100), 96, NotificationCorner.BottomRight); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "座標オーバーフローを拒否しない");
        });

        Run("通知除外は設定成功と0x11の読み戻しの両方が必要", () =>
        {
            int reads = 0;
            var failed = CaptureToastForm.CheckProtection(123, (_, value) => { Equal(0x11u, value); return false; },
                _ => { reads++; return (true, 0x11); });
            Check(!failed.Success && failed.Warning is not null, "設定失敗を見逃す"); Equal(0, reads);
            foreach (uint affinity in new[] { 0u, 1u, 0x10u })
                Check(!CaptureToastForm.CheckProtection(123, (_, _) => true, _ => (true, affinity)).Success, "読み戻しの不一致を許可");
            Check(!CaptureToastForm.CheckProtection(123, (_, _) => true, _ => (false, 0x11)).Success, "読取失敗を許可");
            Check(CaptureToastForm.CheckProtection(123, (_, _) => true, _ => (true, 0x11)).Success, "正しい除外状態を拒否");
        });

        Run("完了表示はフォーカス非取得・クリック透過・タスクバー非表示", () =>
        {
            using var form = NewToast();
            var flags = (CreateParams)typeof(CaptureToastForm).GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            Equal(CaptureToastForm.ToastExtendedStyle, flags.ExStyle & CaptureToastForm.ToastExtendedStyle);
            Equal(0, flags.Style & 0x10000000);
            Check((bool)typeof(CaptureToastForm).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!, "非アクティブ表示でない");
            Check(!form.ShowInTaskbar && !form.TopMost && form.FormBorderStyle == FormBorderStyle.None, "通知のウィンドウ形式が不正");
            foreach (var item in new[] { (Message: 0x21, Result: 3), (Message: 0x84, Result: -1) })
            {
                object[] args = [Message.Create(0, item.Message, 0, 0)];
                typeof(CaptureToastForm).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
                Equal((nint)item.Result, ((Message)args[0]).Result);
            }
        });

        Run("除外失敗時は表示コールバックを呼ばず再ハンドル化も再確認", () =>
        {
            int shows = 0, protects = 0;
            bool permitted = false;
            using var form = NewToast(_ => { protects++; return new CaptureProtection(permitted, permitted ? null : "除外失敗"); }, _ => shows++);
            Check(!form.TryShow("コピー完了", false, NotificationCorner.BottomRight, out string? warning), "除外失敗を表示成功扱い");
            Equal("除外失敗", warning); Equal(0, shows); Check(!form.Visible, "保護なしに表示された");
            permitted = true;
            Check(form.TryShow("コピー完了", false, NotificationCorner.BottomLeft, out warning), "保護成功が失敗扱い");
            Equal(1, shows);
            int previous = protects;
            permitted = false;
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Check(protects > previous, "ハンドル再生成時に確認しない");
            Check(!form.TryShow("再コピー", false, NotificationCorner.BottomRight, out _), "再生成ハンドルを未確認のまま表示");
            Equal(1, shows); Check(!form.Visible, "再生成後に保護なしで表示された");
        });

        Run("完了表示の4行がDPIごとの領域に入りDock故障を検出", () =>
        {
            var all = new CaptureDelivery(CaptureIntent.Screenshot,
                OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, OutputDestination.None, true);
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                using var form = new CaptureToastForm(_ => new CaptureProtection(true, null), _ => { },
                    () => (new Rectangle(-2560, -200, 2560, 1440), dpi));
                Check(form.TryShow(all.Message, true, NotificationCorner.BottomRight, out _), "通知の設定に失敗");
                foreach (Control child in Descendants(form)) _ = child.Handle;
                LayoutTree(form);
                var label = form.Controls.Find("CaptureToastMessage", true).Single();
                Check(label.Width >= (int)(350 * dpi / 96d) && label.Height >= (int)(90 * dpi / 96d), "4行の表示領域不足");
                Size text = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Check(text.Height <= label.Height, $"DPI{dpi}で4行が収まらない {text.Height}/{label.Height}");
                Check(label.Parent!.ClientRectangle.Contains(label.Bounds), "通知文が親領域外");
                var layout = form.Controls.Find("CaptureToastLayout", true).Single();
                layout.Dock = DockStyle.None; layout.Size = new Size(100, 36); LayoutTree(form);
                Check(label.Width < 350 || label.Height < 90, "Dock切断を検出できない");
            }
        });

        Run("通知と効果音は独立ON/OFFで部分成功だけ成功音を出す", () =>
        {
            foreach (bool notification in new[] { false, true })
                foreach (bool audio in new[] { false, true })
                    foreach (var delivery in new[]
                    {
                        new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.None),
                        new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None),
                        new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.File),
                        new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.File)
                    })
                    {
                        var toast = new FakeCaptureToast(); var sound = new FakeCompletionSound(); var timer = new FakeFeedbackTimer();
                        using var feedback = new CaptureFeedback(createToast: () => toast, sound: sound, timer: timer);
                        feedback.Notify(delivery, new AppSettings { NotificationDurationSeconds = notification ? 1 : 0, PlayCaptureSound = audio });
                        Equal(audio && delivery.HasSuccess ? 1 : 0, sound.Requests);
                        Equal(notification && (delivery.HasSuccess || delivery.HasFailure) ? 1 : 0, toast.Shows);
                    }
        });

        Run("通知連発は一窓更新し期限・撮影前・Disposeでタイマー停止", () =>
        {
            int created = 0;
            var toast = new FakeCaptureToast(); var sound = new FakeCompletionSound(); var timer = new FakeFeedbackTimer();
            using var feedback = new CaptureFeedback(createToast: () => { created++; return toast; }, sound: sound, timer: timer);
            var delivery = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None);
            for (int index = 0; index < 10; index++) feedback.Notify(delivery, new AppSettings());
            Equal(1, created); Equal(10, toast.Shows); Equal(10, timer.Starts);
            Equal(1000, timer.Milliseconds); Check(timer.Running, "通知の期限タイマーが動かない");
            timer.Fire(); Check(!timer.Running && !toast.Visible, "期限後も残る");
            feedback.Notify(delivery, new AppSettings()); feedback.HideForCapture();
            Check(!timer.Running && !toast.Visible, "撮影前も通知が残る");
            feedback.Dispose(); feedback.Notify(delivery, new AppSettings());
            Check(timer.Disposed && sound.Disposed && toast.Disposed && !timer.Running, "解放後も通知が動く");
            Equal(11, toast.Shows);
        });

        Run("除外または効果音の失敗は撮影完了を壊さず警告へ返す", () =>
        {
            var warnings = new List<string>();
            var toast = new FakeCaptureToast { Allow = false }; var sound = new FakeCompletionSound(); var timer = new FakeFeedbackTimer();
            using var feedback = new CaptureFeedback(warnings.Add, () => toast, sound, timer);
            feedback.Notify(new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None), new AppSettings());
            Equal(1, sound.Requests); Check(!toast.Visible && !timer.Running, "保護失敗時に表示継続");
            Equal("除外を確認できません", feedback.LastWarning); Equal(1, warnings.Count);
            sound.Throw = true; toast.Allow = true;
            feedback.Notify(new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None), new AppSettings());
            Check(toast.Visible && timer.Running, "音声失敗で表示も失敗扱い");
            Check(feedback.LastWarning!.Contains("効果音"), "音声失敗が警告にならない");
        });

        Run("完了音のWAVは120msモノラルPCMで静かな二音と無音端を持つ", () =>
        {
            byte[] wave = CompletionSound.CreateWave();
            Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4)); Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
            Equal("fmt ", Encoding.ASCII.GetString(wave, 12, 4)); Equal("data", Encoding.ASCII.GetString(wave, 36, 4));
            Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4)); Equal(16, BitConverter.ToInt32(wave, 16));
            Equal((short)1, BitConverter.ToInt16(wave, 20)); Equal((short)1, BitConverter.ToInt16(wave, 22));
            Equal(24000, BitConverter.ToInt32(wave, 24)); Equal(48000, BitConverter.ToInt32(wave, 28));
            Equal((short)2, BitConverter.ToInt16(wave, 32)); Equal((short)16, BitConverter.ToInt16(wave, 34));
            Equal(120, BitConverter.ToInt32(wave, 40) * 1000 / 48000);
            short[] samples = Enumerable.Range(0, (wave.Length - 44) / 2).Select(index => BitConverter.ToInt16(wave, 44 + index * 2)).ToArray();
            Equal((short)0, samples[0]); Equal((short)0, samples[^1]);
            int peak = samples.Max(value => Math.Abs((int)value)); Check(peak is > 2000 and <= 3277, "音量が静音の範囲外");
            Check(samples.Skip(1200).Take(288).All(value => value == 0), "二音の間に無音がない");
            Check(samples.Take(1200).Any(value => value != 0) && samples.Skip(1488).Any(value => value != 0), "二音を含まない");
            Equal(6u, CompletionSound.PlaybackFlags);
        });

        Run("効果音はworkerで重複をまとめ解放後は再生要求を無視", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int calls = 0, callerThread = Environment.CurrentManagedThreadId, workerThread = 0;
            using var sound = new CompletionSound(_ =>
            {
                Interlocked.Increment(ref calls); workerThread = Environment.CurrentManagedThreadId; entered.Set();
                if (!release.Wait(3000)) throw new TimeoutException();
                return true;
            });
            try
            {
                sound.Request(); Check(entered.Wait(3000), "workerが開始しない");
                for (int index = 0; index < 50; index++) sound.Request();
                Equal(1, calls); Check(workerThread != callerThread && sound.IsPlaying, "UIスレッドで同期再生している");
                sound.Dispose(); sound.Request(); Equal(1, calls);
            }
            finally { release.Set(); sound.Pending.GetAwaiter().GetResult(); }
            Check(!sound.IsPlaying, "完了後も再生中扱い");
        });

        Run("効果音workerの失敗は例外伝播せず警告を通知", () =>
        {
            string? reported = null;
            using var sound = new CompletionSound(_ => false, text => reported = text);
            sound.Request(); sound.Pending.GetAwaiter().GetResult();
            Check(sound.LastWarning is not null && reported == sound.LastWarning, "失敗警告がない");
            Check(!sound.IsPlaying, "失敗で再生中状態が残る");
        });

        static CaptureToastForm NewToast(Func<nint, CaptureProtection>? protection = null, Action<CaptureToastForm>? show = null)
            => new(protection ?? (_ => new CaptureProtection(true, null)), show ?? (_ => { }),
                () => (new Rectangle(-1920, 0, 1920, 1040), 96));
    }

    private sealed class FakeCaptureToast : ICaptureToast
    {
        public Rectangle DisplayBounds { get; set; }
        public int DisplayDpi { get; set; } = 96;
        internal int Shows;
        internal bool Allow = true, Visible, Disposed;
        internal Action? Activate;
        internal NotificationActions? Actions;
        public void SetActions(NotificationActions actions) => Actions = actions;
        internal NotificationDisplay Display;
        internal Rectangle? CaptureBounds;
        public bool TryShow(string message, bool hasFailure, NotificationCorner corner, out string? warning,
            Action? activate = null, NotificationDisplay display = NotificationDisplay.Captured, Rectangle? captureBounds = null)
        { Shows++; Visible = Allow; Activate = activate; Display = display; CaptureBounds = captureBounds; warning = Allow ? null : "除外を確認できません"; return Allow; }
        public void Hide() { Visible = false; Activate = null; Actions = null; }
        public void Dispose() { Disposed = true; Visible = false; }
    }

    private sealed class FakeCompletionSound : ICompletionSound
    {
        internal int Requests;
        internal bool Disposed, Throw;
        public string? LastWarning => null;
        public void Request() { Requests++; if (Throw) throw new InvalidOperationException("再生失敗"); }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFeedbackTimer : IFeedbackTimer
    {
        public event Action? Elapsed;
        internal int Starts, Milliseconds;
        internal bool Running, Disposed;
        public void Restart(int milliseconds) { Starts++; Milliseconds = milliseconds; Running = true; }
        public void Stop() => Running = false;
        internal void Fire() => Elapsed?.Invoke();
        public void Dispose() { Disposed = true; Running = false; }
    }
}
