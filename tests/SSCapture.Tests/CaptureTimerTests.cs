using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureTimerTests()
    {
        Run("タイマーCLIの時限・定期引数とPowerShell生成", () =>
        {
            var once = CaptureCommandLine.Parse(["--delay-seconds", "5", "--capture-select"]);
            Equal<CaptureSchedule?>(new CaptureSchedule.Delay(5), once.Schedule);
            var at = new DateTimeOffset(2090, 5, 4, 3, 2, 1, TimeSpan.FromHours(9));
            Equal<CaptureSchedule?>(new CaptureSchedule.At(at),
                CaptureCommandLine.Parse(["--capture-select", "--at", "2090-05-04T03:02:01+09:00"]).Schedule);
            Equal(at.UtcDateTime, ((CaptureSchedule.At)CaptureCommandLine.Parse(["--capture-select", "--at", "2090-05-03T18:02:01Z"]).Schedule!).When.UtcDateTime);
            var periodic = CaptureCommandLine.Parse(["--capture-region", "-10", "-20", "3", "4",
                "--count", "3", "--output-preset", "run", "--interval-seconds", "10"]);
            Equal<CaptureSchedule?>(new CaptureSchedule.Periodic(10, 3), periodic.Schedule);
            Equal(@"& 'C:\SSCapture.exe' '--capture-region' '-10' '-20' '3' '4' '--output-preset' 'run' '--interval-seconds' '10' '--count' '3'",
                CaptureCommandLine.Generate(@"C:\SSCapture.exe", periodic));
            Check(CaptureCommandLine.Handles(["--count", "3"]), "タイマー引数だけで常駐起動にフォールスルーした");
        });
        Run("タイマーCLIは矛盾・不足・期限切れ・オフセットなしを拒否", () =>
        {
            string[][] invalid = [["--delay-seconds", "0"], ["--delay-seconds", "-1"], ["--delay-seconds", "2147483648"],
                ["--delay-seconds", "1", "--delay-seconds", "2"], ["--delay-seconds", "1", "--at", "2090-05-04T03:02:01+09:00"],
                ["--interval-seconds", "10"], ["--count", "3"], ["--interval-seconds", "0", "--count", "1"],
                ["--interval-seconds", "10", "--count", "0"], ["--count", "4", "--delay-seconds", "3"],
                ["--at", "2090-05-04T03:02:01"], ["--at", "2000-01-01T00:00:00Z"], ["--at", "--count", "1"]];
            foreach (string[] args in invalid)
                CaptureCommandReject(() => CaptureCommandLine.Parse(["--capture-select", .. args]));
            CaptureCommandReject(() => CaptureCommandLine.Parse(["--delay-seconds", "5"]));
            CaptureCommandReject(() => CaptureCommandLine.Parse(["--capture-region", "1", "--delay-seconds", "1", "2", "3", "4"]));
        });
        Run("時限の指定秒数は単調時計、指定日時はオフセット時刻を使う", () =>
        {
            var clock = new TimerTestClock();
            int captures = 0;
            Task Take(DateTimeOffset at)
            {
                Equal(TimeSpan.FromHours(9), at.Offset);
                captures++;
                return Task.CompletedTask;
            }
            var delay = new CaptureReservation(new CaptureSchedule.Delay(2), clock, Take);
            delay.Poll(() => null);
            clock.Now += TimeSpan.FromHours(2);
            Advance(delay, clock, 1);
            Equal(0, captures);
            Advance(delay, clock, 1);
            Equal(1, captures);
            Check(delay.Completion.IsCompletedSuccessfully, "時限が完了していない");
            var absolute = new CaptureReservation(new CaptureSchedule.At(clock.Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(9))), clock, Take);
            clock.Now += TimeSpan.FromMinutes(1);
            absolute.Poll(() => null);
            Equal(2, captures);
        });
        Run("ロック・復帰時に逃した時限は理由を返して終了", () =>
        {
            foreach (bool resumed in new[] { false, true })
            {
                var clock = new TimerTestClock();
                var timer = new CaptureReservation(new CaptureSchedule.Delay(1), clock, _ => throw new Exception("撮影した"));
                clock.Advance(1);
                timer.Poll(() => "画面ロック中です。", resumed: resumed);
                string reason = timer.Completion.Exception!.InnerException!.Message;
                Check(reason.Contains(resumed ? "中断中" : "ロック中", StringComparison.Ordinal), reason);
                Equal<DateTimeOffset?>(null, timer.NextDue);
            }
        });
        Run("定期は初回・10秒・20秒の予定を維持し、成功枚数で完了", () =>
        {
            var clock = new TimerTestClock();
            DateTimeOffset started = clock.Now;
            var times = new List<TimeSpan>();
            var timer = new CaptureReservation(new CaptureSchedule.Periodic(10, 3), clock, at =>
            {
                times.Add(at - started);
                return Task.CompletedTask;
            });
            timer.Poll(() => null);
            Equal(started.AddSeconds(10), timer.NextDue!.Value);
            Advance(timer, clock, 20);
            Check(times.SequenceEqual([TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]), "一定間隔になっていない");
            Check(timer.Completion.IsCompletedSuccessfully, "指定枚数で完了していない");
        });
        Run("定期は処理超過・ロック・スリープ時の欠番を再実行しない", () =>
        {
            var clock = new TimerTestClock();
            var first = new TaskCompletionSource();
            int captures = 0;
            var timer = new CaptureReservation(new CaptureSchedule.Periodic(10, 3), clock, _ =>
                ++captures == 1 ? first.Task : Task.CompletedTask);
            timer.Poll(() => null);
            Advance(timer, clock, 10);
            Equal(1, captures);
            first.SetResult();
            Advance(timer, clock, 10, "ロック中");
            clock.Advance(27);
            timer.Poll(() => null);
            Equal(1, captures);
            Equal(clock.Now.AddSeconds(3), timer.NextDue!.Value);
            Advance(timer, clock, 3);
            Equal(2, captures);
            Check(!timer.Completion.IsCompleted, "欠番を成功枚数に含めた");
            Advance(timer, clock, 10);
            Equal(3, captures);
            Check(timer.Completion.IsCompletedSuccessfully, "成功枚数で終了しなかった");
        });
        Run("停止は進行中の出力を待ち次回を取り消す", () =>
        {
            var clock = new TimerTestClock();
            var active = new TaskCompletionSource();
            int captures = 0;
            var timer = new CaptureReservation(new CaptureSchedule.Periodic(1, null), clock, _ => { captures++; return active.Task; });
            timer.Poll(() => null);
            timer.Stop();
            Check(!timer.Completion.IsCompleted, "進行中の出力を待っていない");
            Equal<DateTimeOffset?>(null, timer.NextDue);
            Advance(timer, clock, 5);
            active.SetResult();
            Check(timer.Completion.IsCompletedSuccessfully, "出力完了後も停止しない");
            Advance(timer, clock, 5);
            Equal(1, captures);
        });
        Run("撮影対象消失・出力不能は残りの予約を止める", () =>
        {
            foreach (string message in new[] { "対象消失", "出力不能" })
            {
                var clock = new TimerTestClock();
                int calls = 0;
                var timer = new CaptureReservation(new CaptureSchedule.Periodic(1, 3), clock, _ =>
                {
                    calls++;
                    return Task.FromException(new IOException(message));
                });
                timer.Poll(() => null);
                Advance(timer, clock, 3);
                Equal(1, calls);
                Equal(message, timer.Completion.Exception!.InnerException!.Message);
                Equal<DateTimeOffset?>(null, timer.NextDue);
            }
        });
        Run("予約ウィンドウは移動を追跡し別ウィンドウに乗り換えない", () =>
        {
            var target = new CaptureWindow(new Rectangle(20, 30, 400, 200), "試験窓", 8, 2, 3, ClassName: "Test");
            var recipe = LastCaptureRecipe.Create(0, target.Bounds, null, new AppSettings(), [],
                new CaptureReplaySource.Windows([target], Isolated: true, IncludeOwned: true), _ => 123);
            var moved = target with { Bounds = new(-500, 50, 400, 200), Title = "移動した窓" };
            var foreign = target with { Handle = 9, ProcessId = 90 };
            Equal(moved, recipe.ResolveWindows([foreign, moved], _ => 123, requireOriginalWindow: true).Single());
            Check(recipe.UseIsolatedWindow && recipe.IncludeOwnedWindows, "選択した撮影方法を失った");
            var replacement = target with { Handle = 10 };
            bool rejected = false;
            try { _ = recipe.ResolveWindows([replacement], _ => 123, requireOriginalWindow: true); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "別のハンドルを対象にした");
            Equal(replacement, recipe.ResolveWindows([replacement], _ => 123).Single());
        });
        Run("予約したモニター・登録範囲は番号でなく識別子を追跡する", () =>
        {
            var main = new DisplayLayout("primary", "メイン", new Rectangle(0, 0, 1000, 800), 96, 96);
            var second = new DisplayLayout("secondary", "試験モニター", new Rectangle(1000, 0, 800, 600), 96, 96);
            DisplayLayout[] initial = [main, second];
            var recipe = LastCaptureRecipe.Create(0, second.Bounds, null, new AppSettings(), initial,
                new CaptureReplaySource.Monitors([second.Bounds]));
            var registered = RegionCoordinates.FromBounds("登録", new Rectangle(1100, 50, 600, 400), initial);
            var moved = second with { Bounds = new(-800, 0, 800, 600) };
            Equal(moved.Bounds, recipe.RegionsFor([moved, main])!.Single());
            Equal(new Rectangle(-700, 50, 600, 400), RegionCoordinates.Resolve(registered, [moved, main]));
            bool rejected = false;
            try { _ = recipe.RegionsFor([main, second with { Identity = "replacement" }]); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "同じ番号・座標の別モニターへ乗り換えた");
        });
        Run("定期の予約用出力は削除済み初期IDの代わりに既定の保存先と規則を使う", () =>
        {
            var settings = CaptureCommandSettings();
            var fallback = settings.CapturePresets.Single(p => p.IsDefault) with { FileNamePattern = "fallback.{ext}" };
            settings = settings with { CapturePresets = [fallback] };
            var resolved = CaptureCommandLine.Resolve(new() { Schedule = new CaptureSchedule.Periodic(10, 3) }, settings);
            Equal(fallback.SaveDirectory, resolved.Settings.SaveDirectory);
            Equal("fallback.{ext}", resolved.Settings.FileNamePattern);
            Equal(OutputDestination.File, resolved.Settings.DefaultDestination);
            Equal(OutputDestination.Clipboard, CaptureCommandLine.Resolve(new(), settings).Settings.DefaultDestination);
        });
        Run("定期の予約用出力は合成画像を保存しOCR成功・取得不能・連番衝突を処理", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                AppSettings settings = TimerOutputSettings(directory, "fixture_{ocr}_{##}.{ext}");
                var periodic = new CaptureSchedule.Periodic(1, 3);
                var request = new CaptureCommandRequest(new Rectangle(0, 0, 4, 4)) { Schedule = periodic };
                var command = CaptureCommandLine.Resolve(request, settings);
                Equal(OutputDestination.File, command.Settings.DefaultDestination);
                Check(!command.Settings.OpenFolderAfterSave, "予約専用の出力でフォルダー表示がON");
                int copies = 0, launches = 0, recognized = 0, frames = 0;
                using var output = new OutputRouter(Path.Combine(directory, "work"),
                    _ => { copies++; return Task.CompletedTask; }, launch: _ => launches++, readDisplays: () => []);
                var shot = CaptureTimerShot.Fixed(request.Region!.Value, command, (_, _) =>
                {
                    frames++;
                    var image = new Bitmap(4, 4);
                    image.SetPixel(0, 0, Color.FromArgb(25 * frames, 0, 0));
                    return Task.FromResult<(Bitmap, CaptureMetadata?)>((image, null));
                });
                Task<OcrResult> Recognize(Bitmap image, string _, CancellationToken __)
                {
                    int call = ++recognized;
                    Equal(25 * call, (int)image.GetPixel(0, 0).R);
                    return Task.FromResult(call == 2 ? new OcrResult([], "取得不能") : new OcrResult(["先頭", "次の行"]));
                }
                for (int i = 0; i < 3; i++) await shot.DeliverAsync(output, DateTimeOffset.Now, Recognize);
                Equal(3, frames); Equal(3, recognized); Equal(0, copies); Equal(0, launches);
                string[] names = Directory.GetFiles(command.Settings.SaveDirectory).Select(path => Path.GetFileName(path)!).ToArray();
                Check(names.Contains("fixture_先頭_01.png") && names.Contains("fixture_先頭_02.png")
                    && names.Contains("fixture_OCRなし_01.png"), string.Join(", ", names));
                Equal(OutputDestination.Clipboard, settings.DefaultDestination);
            })));
        Run("明示出力は定期の各回へ適用し、OCRなしの規則では認識を呼ばない", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var settings = TimerOutputSettings(directory, "fixture.{ext}");
                var request = new CaptureCommandRequest(new Rectangle(0, 0, 4, 4), OutputPresetId: "initial-output-file")
                { Schedule = new CaptureSchedule.Periodic(1, 2) };
                var command = CaptureCommandLine.Resolve(request, settings);
                Equal(OutputDestination.File | OutputDestination.ExternalApp, command.Settings.DefaultDestination);
                Check(command.Settings.OpenFolderAfterSave, "明示プリセットの保存後動作を失った");
                int launches = 0, ocrCalls = 0;
                using var output = new OutputRouter(Path.Combine(directory, "work"), launch: _ => launches++, readDisplays: () => []);
                var shot = CaptureTimerShot.Fixed(request.Region!.Value, command, (_, _) =>
                    Task.FromResult<(Bitmap, CaptureMetadata?)>((new Bitmap(4, 4), null)));
                for (int i = 0; i < 2; i++) await shot.DeliverAsync(output, DateTimeOffset.Now, (_, _, _) =>
                { ocrCalls++; return Task.FromResult(new OcrResult([])); });
                Equal(4, launches);
                Equal(0, ocrCalls);
                Equal(2, Directory.GetFiles(command.Settings.SaveDirectory).Length);
            })));
        Run("予約用の選択画面は対象を返し確定時の画像取得や出力選択を行わない", () =>
        {
            Rectangle bounds = new(0, 0, 320, 240);
            var target = new CaptureWindow(new Rectangle(10, 20, 50, 60), "予約対象", 8, 2, 3);
            var session = new SessionController(new CaptureOptions(CaptureMode.Window, CaptureIntent.Screenshot,
                OutputDestination.File, FreezeFrame: false) { SelectionOnly = true },
                new DesktopSnapshot(new Bitmap(bounds.Width, bounds.Height), bounds, [bounds]), [target.Bounds],
                () => throw new InvalidOperationException("確定時の画像を取得した"), findWindow: _ => target);
            try
            {
                Check(!session.SelectionMenu.Command("CapturePresetMenu").Available
                    && !session.SelectionMenu.Command("CaptureOutputGroup").Available, "予約時の出力条件を変える項目が表示された");
                session.CompleteSelection(new(target.Bounds, null, OutputDestination.File, target.Bounds.Location));
                using var outcome = session.Completion.GetAwaiter().GetResult();
                Check(outcome.Image is null && outcome.Selection?.ReplaySource is CaptureReplaySource.Windows,
                    "対象だけを予約結果として返さなかった");
            }
            finally { session.Cancel(); }
        });
        Run("画面選択の予約でEscを受けると画素を取得せず終了", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                int selections = 0;
                var command = CaptureCommandLine.Resolve(new() { Schedule = new CaptureSchedule.Delay(1) }, CaptureCommandSettings());
                using var output = new OutputRouter(directory);
                int exit = await CaptureCommandRunner.ExecuteAsync(command, output, select: options =>
                {
                    selections++;
                    Check(options.SelectionOnly, "予約時に最終画像取得を要求した");
                    return Task.FromResult(new CaptureOutcome(null, null));
                });
                Equal(2, exit);
                Equal(1, selections);
            })));
        Run("予約画面は既定出力・定期の保存先と規則・停止までの予約を確認できる", () =>
        {
            using var form = new CaptureTimerForm(CaptureCommandSettings(), []);
            var save = (TextBox)form.Controls.Find("CaptureTimerSaveDirectory", true).Single();
            var pattern = (TextBox)form.Controls.Find("CaptureTimerFileName", true).Single();
            var output = (ComboBox)form.Controls.Find("CaptureTimerOutput", true).Single();
            Equal("既定", output.SelectedItem!.ToString());
            Equal("", save.Text);
            ((ComboBox)form.Controls.Find("CaptureScheduleMode", true).Single()).SelectedIndex = 2;
            Equal("ファイル保存のみ（予約用）", output.SelectedItem!.ToString());
            Check(Path.IsPathFullyQualified(save.Text) && pattern.Text.Length > 0, "予約用出力の保存条件が見えない");
            var interval = (NumericUpDown)form.Controls.Find("CaptureScheduleInterval", true).Single();
            interval.Value = 0;
            CaptureCommandClick(form, "CaptureTimerReserve");
            Check(form.Setup is null, "0秒間隔で予約が成立した");
            interval.Value = 10;
            ((CheckBox)form.Controls.Find("CaptureScheduleUnlimited", true).Single()).Checked = true;
            CaptureCommandClick(form, "CaptureTimerReserve");
            Check(form.Setup is { SelectionMode: CaptureMode.Rectangle }, "対象を予約できない");
            Equal<CaptureSchedule?>(new CaptureSchedule.Periodic(10, null), form.Setup!.Command.Request.Schedule);
        });
        Run("設定の撮影コマンドはタイマー句を生成し変更時に旧コマンドを消す", () =>
        {
            using var control = new CaptureCommandControl(CaptureCommandSettings, (_, error) => Check(!error, "生成に失敗"), @"C:\SSCapture.exe");
            var mode = (ComboBox)control.Controls.Find("CaptureScheduleMode", true).Single();
            var output = (ComboBox)control.Controls.Find("CaptureCommandOutput", true).Single();
            using var tips = new ToolTip();
            SettingsHelp.Apply(control, tips);
            Check(!string.IsNullOrWhiteSpace(tips.GetToolTip(mode)), "タイマー選択のツールチップがない");
            mode.SelectedIndex = 1;
            CaptureCommandClick(control, "CaptureCommandGenerate");
            var text = (TextBox)control.Controls.Find("CaptureCommandText", true).Single();
            Equal(@"& 'C:\SSCapture.exe' '--capture-select' '--delay-seconds' '5'", text.Text);
            mode.SelectedIndex = 3;
            Equal("", text.Text);
            Equal("ファイル保存のみ（予約用）", output.SelectedItem!.ToString());
            CaptureCommandClick(control, "CaptureCommandGenerate");
            Equal(@"& 'C:\SSCapture.exe' '--capture-select' '--interval-seconds' '10' '--count' '3'", text.Text);
        });
    }

    private static AppSettings TimerOutputSettings(string directory, string pattern)
    {
        var settings = CaptureCommandSettings();
        string editor = Path.Combine(directory, "fixture.exe");
        File.WriteAllBytes(editor, [0]);
        var application = new ExternalApplication { Id = "timer-editor", Name = "試験", Executable = editor };
        var output = new CapturePreset { Id = "initial-output-file", Name = "試験保存", FileNamePattern = pattern,
            SaveDirectory = Path.Combine(directory, "saved"), Destination = OutputDestination.File | OutputDestination.ExternalApp,
            ImageEditorId = application.Id, OpenFolderAfterSave = true };
        return settings with { CapturePresets = [settings.CapturePresets.Single(p => p.IsDefault), output], ImageEditors = [.. settings.ImageEditors, application] };
    }

    private static void Advance(CaptureReservation timer, TimerTestClock clock, int seconds, string? unavailable = null)
    {
        for (int i = 0; i < seconds; i++)
        {
            clock.Advance(1);
            timer.Poll(() => unavailable);
        }
    }

    private sealed class TimerTestClock : TimeProvider
    {
        private static readonly TimeZoneInfo LocalZone = TimeZoneInfo.CreateCustomTimeZone("試験時刻", TimeSpan.FromHours(9), "試験時刻", "試験時刻");
        private long timestamp;
        public override TimeZoneInfo LocalTimeZone => LocalZone;
        internal DateTimeOffset Now { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(int seconds)
        {
            timestamp += TimeSpan.FromSeconds(seconds).Ticks;
            Now = Now.AddSeconds(seconds);
        }
    }
}
